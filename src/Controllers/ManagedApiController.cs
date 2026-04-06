using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

// ── Programmatic SA surface — public port, always auth-gated ─────────────────
// Accessible at /api/manage/* from external service accounts.
// Requires SuperAdmin PAT or client_credentials access token with super_admin role.

[ApiController]
[Route("api/manage")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class ManagedApiController(
    RediensIamDbContext db,
    ManagedApiServices svc,
    AppConfig appConfig,
    ILogger<ManagedApiController> logger) : ControllerBase
{
    // Unwrap bundle (S107)
    private HydraService hydra         => svc.Hydra;
    private KetoService keto           => svc.Keto;
    private PasswordService passwords   => svc.Passwords;
    private AuditLogService audit       => svc.Audit;
    private IEmailService emailService  => svc.Email;
    private static readonly string[] OAuth2GrantTypes = ["authorization_code", "refresh_token"];
    private static readonly string[] OAuth2ResponseTypes = ["code"];

    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid GetActorId() => Claims.ParsedUserId;

    // ── Organisations ─────────────────────────────────────────────────────────

    [HttpGet("organizations")]
    public async Task<IActionResult> ListOrgs()
    {
        var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt })
            .ToListAsync();
        return Ok(orgs);
    }

    [HttpGet("organizations/{id}")]
    public async Task<IActionResult> GetOrg(Guid id)
    {
        var org = await db.Organisations
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt, o.UpdatedAt, o.OrgListId, o.CreatedBy })
            .FirstOrDefaultAsync();
        if (org == null) return NotFound();
        return Ok(org);
    }

    [HttpPost("organizations")]
    public async Task<IActionResult> CreateOrg([FromBody] CreateOrgRequest body)
    {
        var actorId = GetActorId();

        var orgList = new UserList { Name = $"{body.Name} Org List", Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(orgList);
        await db.SaveChangesAsync();

        var org = new Organisation
        {
            Name = body.Name, Slug = body.Slug, OrgListId = orgList.Id,
            Active = true, CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        orgList.OrgId = org.Id;
        await db.SaveChangesAsync();

        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, org.Id.ToString(), "org", $"{Roles.KetoSystemNamespace}:{Roles.KetoSystemObject}");
        await audit.RecordAsync(org.Id, null, actorId, "org.created", "organisation", org.Id.ToString());
        return Created($"/api/manage/organizations/{org.Id}", new { org.Id, org.Name, org.Slug, org_list_id = orgList.Id });
    }

    // ── Projects ──────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/projects")]
    public async Task<IActionResult> ListProjects(Guid id)
    {
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();
        var projects = await db.Projects
            .Where(p => p.OrgId == id)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active, p.HydraClientId, p.CreatedAt })
            .ToListAsync();
        return Ok(projects);
    }

    [HttpPost("organizations/{id}/projects")]
    public async Task<IActionResult> CreateProject(Guid id, [FromBody] AdminCreateProjectRequest body)
    {
        var actorId = GetActorId();
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();

        var project = new Project
        {
            OrgId = id, Name = body.Name, Slug = body.Slug,
            RequireRoleToLogin = body.RequireRoleToLogin ?? false,
            Active = true, CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        try
        {
            await hydra.CreateOAuth2ClientAsync(new
            {
                client_id      = $"client_{project.Id}",
                client_name    = $"Project: {project.Name}",
                redirect_uris  = body.RedirectUris ?? [],
                grant_types    = OAuth2GrantTypes,
                response_types = OAuth2ResponseTypes,
                scope          = "openid profile offline_access",
                token_endpoint_auth_method = "none",
                metadata       = new { project_id = project.Id.ToString(), org_id = id.ToString() }
            });
            project.HydraClientId = $"client_{project.Id}";
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hydra client creation failed for project {ProjectId} — rolling back", project.Id);
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            return StatusCode(502, new { error = "hydra_unavailable", detail = ex.Message });
        }

        await keto.WriteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), "org", $"{Roles.KetoOrgsNamespace}:{id}");
        await audit.RecordAsync(id, project.Id, actorId, "project.created", "project", project.Id.ToString());
        return Created($"/api/manage/organizations/{id}/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    // ── User Lists ────────────────────────────────────────────────────────────

    [HttpPost("userlists")]
    public async Task<IActionResult> CreateUserList([FromBody] AdminCreateUserListRequest body)
    {
        var ul = new UserList { Name = body.Name, OrgId = body.OrgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/api/manage/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    [HttpPost("userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] AdminCreateUserRequest body)
    {
        var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id);
        if (ul == null) return NotFound();

        var normalizedEmail = body.Email.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.UserListId == id && u.Email == normalizedEmail))
            return Conflict(new { error = "email_already_exists" });

        var username = body.Username ?? body.Email.Split('@')[0];
        string discriminator;
        do { discriminator = Random.Shared.Next(1000, 9999).ToString(); }
        while (await db.Users.AnyAsync(u => u.UserListId == id && u.Username == username && u.Discriminator == discriminator));

        var emailVerified = body.EmailVerified ?? false;
        var isInvite = string.IsNullOrEmpty(body.Password);
        var user = new User
        {
            UserListId    = id, Username = username,
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash  = isInvite ? null : passwords.Hash(body.Password!),
            EmailVerified = emailVerified,
            EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow : null,
            Active    = !isInvite,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{user.Id}");
        if (ul.OrgId == null && ul.Immovable)
            await keto.WriteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");

        var assignedProjects = await db.Projects.Where(p => p.AssignedUserListId == id).ToListAsync();
        foreach (var project in assignedProjects)
            await keto.AssignDefaultRoleAsync(project, user);

        if (isInvite)
        {
            var raw  = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(raw)));
            db.EmailTokens.Add(new EmailToken
            {
                UserId    = user.Id,
                Kind      = "invite",
                TokenHash = hash,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(appConfig.InviteExpiryHours),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var inviteUrl = $"{appConfig.PublicUrl}/auth/invite/complete?token={Uri.EscapeDataString(raw)}";
            var orgName   = ul.Organisation?.Name ?? "the organization";
            await emailService.SendInviteAsync(user.Email, inviteUrl, orgName);
        }

        return Created($"/api/manage/userlists/{id}/users/{user.Id}", new
        {
            user.Id,
            username       = $"{user.Username}#{user.Discriminator}",
            user.Email,
            invite_pending = isInvite
        });
    }
}
