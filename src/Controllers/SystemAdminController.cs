using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[Route("admin")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class SystemAdminController(
    RediensIamDbContext db,
    OrgAdminServices svc,
    AppConfig appConfig,
    ILogger<SystemAdminController> logger) : ControllerBase
{
    // Unwrap bundle (S107)
    private HydraService hydra         => svc.Hydra;
    private KetoService keto           => svc.Keto;
    private PasswordService passwords   => svc.Passwords;
    private AuditLogService audit       => svc.Audit;
    private IEmailService emailService  => svc.Email;
    private IDistributedCache cache     => svc.Cache;
    private static readonly string[] OAuth2GrantTypes   = ["authorization_code", "refresh_token"];
    private static readonly string[] OAuth2ResponseTypes = ["code"];
    private static readonly string[] BuiltInScopes       = ["openid", "profile", "offline_access"];
    private const string AuditOrg = "organisation";
    private const string KindInvite = "invite";

    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid GetActorId() => Claims.ParsedUserId;

    // ── Organisations ─────────────────────────────────────────────────────────

    [HttpGet("organizations")]
    public async Task<IActionResult> ListOrgs()
    {
var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt }).ToListAsync();
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
        await audit.RecordAsync(org.Id, null, actorId, "org.created", AuditOrg, org.Id.ToString());
        return Created($"/admin/organizations/{org.Id}", new { org.Id, org.Name, org.Slug, org_list_id = orgList.Id });
    }

    [HttpPatch("organizations/{id}")]
    public async Task<IActionResult> UpdateOrg(Guid id, [FromBody] UpdateOrgRequest body)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        if (body.Name != null) org.Name = body.Name;
        if (body.AuditRetentionDays.HasValue) org.AuditRetentionDays = body.AuditRetentionDays == -1 ? null : body.AuditRetentionDays;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { org.Id, org.Name, org.AuditRetentionDays });
    }

    [HttpPost("organizations/{id}/suspend")]
    public async Task<IActionResult> SuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = false; org.SuspendedAt = DateTimeOffset.UtcNow; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.suspended", AuditOrg, id.ToString());
        return Ok(new { message = "org_suspended" });
    }

    [HttpPost("organizations/{id}/unsuspend")]
    public async Task<IActionResult> UnsuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = true; org.SuspendedAt = null; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.unsuspended", AuditOrg, id.ToString());
        return Ok(new { message = "org_unsuspended" });
    }

    [HttpDelete("organizations/{id}")]
    public async Task<IActionResult> DeleteOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        var projects = await db.Projects.Where(p => p.OrgId == id).ToListAsync();
        foreach (var p in projects)
        {
            if (p.HydraClientId != null)
            {
                try { await hydra.DeleteOAuth2ClientAsync(p.HydraClientId); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete Hydra client {ClientId} during org {OrgId} deletion", p.HydraClientId, id); }
            }
            await keto.DeleteAllProjectTuplesAsync(p.Id.ToString());
        }
        db.Projects.RemoveRange(projects);

        var orgRoles = await db.OrgRoles.Where(r => r.OrgId == id).ToListAsync();
        db.OrgRoles.RemoveRange(orgRoles);

        // Query only list IDs — avoid loading UserList entities into EF's change tracker,
        // which would trigger EF's own cascade and create a circular-dependency error.
        var listIds = await db.UserLists.Where(ul => ul.OrgId == id).Select(ul => ul.Id).ToListAsync();
        foreach (var listId in listIds)
        {
            var users = await db.Users.Where(u => u.UserListId == listId).ToListAsync();
            foreach (var userId in users.Select(u => u.Id))
            {
                await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, listId.ToString(), "member", $"user:{userId}");
                try { await hydra.RevokeSessionsAsync($"{id}:{userId}"); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to revoke Hydra sessions for user {UserId} during org {OrgId} deletion", userId, id); }
            }
            db.Users.RemoveRange(users);
        }
        // Save the user deletions before tackling the org+lists circular FK.
        await db.SaveChangesAsync();

        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), "org", $"{Roles.KetoSystemNamespace}:{Roles.KetoSystemObject}");

        // Use raw SQL so PostgreSQL handles the circular FK (org.OrgListId ↔ list.OrgId)
        // in a single statement where both sides are deleted atomically.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM organisations WHERE \"Id\" = {0}", id);
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public async Task<IActionResult> SearchUsers([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
var query = db.Users.AsQueryable();
        if (!string.IsNullOrEmpty(q))
            query = query.Where(u => u.Email.Contains(q) || u.Username.Contains(q));
        var users = await query
            .Include(u => u.UserList)
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.Active, u.UserListId, u.LastLoginAt, OrgId = u.UserList.OrgId })
            .ToListAsync();
        return Ok(users);
    }

    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
var user = await db.Users
            .Include(u => u.UserList).ThenInclude(ul => ul.Organisation)
            .FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgRoles = await db.OrgRoles
            .Where(r => r.UserId == id)
            .Select(r => new { r.Role, r.OrgId, r.ScopeId })
            .ToListAsync();
        var isSystemAdmin = user.UserList.OrgId == null && user.UserList.Immovable;
        return Ok(new
        {
            user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName,
            user.Phone, user.Active, user.EmailVerified, user.PhoneVerified,
            user.TotpEnabled, user.WebAuthnEnabled,
            user.LockedUntil, user.FailedLoginCount,
            user.LastLoginAt, user.CreatedAt, user.UpdatedAt,
            user_list_id = user.UserListId,
            org_id       = user.UserList.OrgId,
            org_name     = user.UserList.Organisation?.Name,
            is_system_admin = isSystemAdmin,
            roles = orgRoles
        });
    }

    [HttpPatch("users/{id}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] UpdateUserRequest body)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        UserHelpers.ApplyUpdate(user, body, passwords);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.updated", "user", id.ToString());
        return Ok(new { user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName, user.Phone, user.Active, user.EmailVerified, user.LockedUntil, user.FailedLoginCount });
    }

    [HttpPost("users/{id}/unlock")]
    public async Task<IActionResult> UnlockUser(Guid id)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        user.LockedUntil      = null;
        user.FailedLoginCount = 0;
        user.UpdatedAt        = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.unlocked", "user", id.ToString());
        return Ok(new { user.Id, message = "user_unlocked" });
    }

    [HttpGet("users/{id}/sessions")]
    public async Task<IActionResult> ListSessions(Guid id)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgId = user.UserList.OrgId?.ToString() ?? "";
        var sessions = await hydra.ListConsentSessionsAsync($"{orgId}:{id}");
        return Ok(sessions.Select(s => new
        {
            client_id   = s.ConsentRequest?.Client?.ClientId,
            client_name = s.ConsentRequest?.Client?.ClientName,
            scopes      = s.GrantedScopes,
            created_at  = s.ConsentRequest?.RequestedAt,
            expires_at  = s.ExpiresAt
        }));
    }

    [HttpDelete("users/{id}/sessions")]
    public async Task<IActionResult> ForceLogout(Guid id)
    {
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgId = user.UserList.OrgId?.ToString() ?? "";
        await hydra.RevokeSessionsAsync($"{orgId}:{id}");
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.force_logout", "user", id.ToString());
        return Ok(new { message = "sessions_revoked" });
    }


    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("userlists")]
    public async Task<IActionResult> ListAllUserLists([FromQuery] Guid? org_id)
    {
var query = db.UserLists.AsQueryable();
        if (org_id.HasValue) query = query.Where(ul => ul.OrgId == org_id);
        var lists = await query
            .Select(ul => new {
                ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt,
                OrgName = ul.Organisation != null ? ul.Organisation.Name : null
            }).ToListAsync();
        return Ok(lists);
    }

    [HttpGet("userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id);
        if (ul == null) return NotFound();
        return Ok(new
        {
            ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt,
            org_name   = ul.Organisation?.Name,
            user_count = await db.Users.CountAsync(u => u.UserListId == id)
        });
    }

    [HttpGet("userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id)) return NotFound();
        var users = await db.Users
            .Where(u => u.UserListId == id)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost("userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] AdminCreateUserRequest body)
    {
        var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id);
        if (ul == null) return NotFound();
        var username = body.Username ?? body.Email.Split('@')[0];
        var discriminator = await UserHelpers.GenerateDiscriminatorAsync(db, id, username);
        var emailVerified = body.EmailVerified ?? false;
        var isInvite = string.IsNullOrEmpty(body.Password);
        var user = new User
        {
            UserListId = id, Username = username,
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash = isInvite ? null : passwords.Hash(body.Password!),
            EmailVerified = emailVerified,
            EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow : null,
            Active = !isInvite, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
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
                Kind      = KindInvite,
                TokenHash = hash,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(appConfig.InviteExpiryHours),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var inviteUrl = $"{appConfig.PublicUrl}/auth/invite/complete?token={Uri.EscapeDataString(raw)}";
            var orgName   = ul.Organisation?.Name ?? "the organization";
            await emailService.SendInviteAsync(user.Email, inviteUrl, orgName);
        }

        return Created($"/admin/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email,
            invite_pending = isInvite
        });
    }

    [HttpDelete("userlists/{id}/users/{uid}")]
    public async Task<IActionResult> RemoveUserFromList(Guid id, Guid uid)
    {
var ul   = await db.UserLists.FindAsync(id);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id);
        if (user == null) return NotFound();
        await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{uid}");
        if (ul?.OrgId == null && ul?.Immovable == true)
            await keto.DeleteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{uid}");
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("userlists")]
    public async Task<IActionResult> AdminCreateUserList([FromBody] AdminCreateUserListRequest body)
    {
var ul = new UserList { Name = body.Name, OrgId = body.OrgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/admin/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    // ── Org Admins ────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/admins")]
    public async Task<IActionResult> ListOrgAdmins(Guid id)
    {
var orgRoles = await db.OrgRoles.Where(r => r.OrgId == id).Include(r => r.User).ToListAsync();
        var projectIds = orgRoles.Where(r => r.ScopeId.HasValue).Select(r => r.ScopeId!.Value).Distinct().ToList();
        var projects = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Ok(orgRoles.Select(r => new
        {
            r.Id, r.OrgId, r.UserId, r.Role, r.ScopeId, r.GrantedAt,
            user_name  = $"{r.User.Username}#{r.User.Discriminator}",
            user_email = r.User.Email,
            scope_name = r.ScopeId.HasValue && projects.TryGetValue(r.ScopeId.Value, out var p) ? p.Name : null
        }));
    }

    [HttpPost("organizations/{id}/admins")]
    public async Task<IActionResult> AssignOrgAdmin(Guid id, [FromBody] AssignOrgAdminRequest body)
    {
var existing = await db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == id && r.UserId == body.UserId && r.Role == body.Role && r.ScopeId == body.ScopeId);
        if (existing != null) return Ok(new { existing.Id });
        var role = new OrgRole
        {
            OrgId = id, UserId = body.UserId, Role = body.Role,
            ScopeId = body.ScopeId, GrantedBy = GetActorId(), GrantedAt = DateTimeOffset.UtcNow
        };
        db.OrgRoles.Add(role);
        await db.SaveChangesAsync();
        var ketoSubject = body.ScopeId.HasValue ? $"user:{body.UserId}|project:{body.ScopeId}" : $"user:{body.UserId}";
        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), body.Role, ketoSubject);
        return Created($"/admin/organizations/{id}/admins/{role.Id}", new { role.Id });
    }

    [HttpDelete("organizations/{id}/admins/{roleId}")]
    public async Task<IActionResult> RemoveOrgAdmin(Guid id, Guid roleId)
    {
var role = await db.OrgRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.OrgId == id);
        if (role == null) return NotFound();
        db.OrgRoles.Remove(role);
        await db.SaveChangesAsync();
        var ketoSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), role.Role, ketoSubject);
        return NoContent();
    }


    // ── Projects (admin scope) ────────────────────────────────────────────────

    [HttpGet("projects")]
    public async Task<IActionResult> AdminListAllProjects()
    {
var projects = await db.Projects
            .Join(db.Organisations, p => p.OrgId, o => o.Id,
                (p, o) => new {
                    p.Id, p.Name, p.Slug, p.Active, p.OrgId,
                    OrgName = o.Name, p.HydraClientId, p.CreatedAt
                })
            .OrderBy(p => p.OrgName).ThenBy(p => p.Name)
            .ToListAsync();
        return Ok(projects);
    }


    [HttpPost("organizations/{id}/projects")]
    public async Task<IActionResult> AdminCreateProject(Guid id, [FromBody] AdminCreateProjectRequest body)
    {
var actorId = GetActorId();
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
                client_id    = $"client_{project.Id}",
                client_name  = $"Project: {project.Name}",
                redirect_uris = body.RedirectUris ?? [],
                grant_types  = OAuth2GrantTypes,
                response_types = OAuth2ResponseTypes,
                scope        = "openid profile offline_access",
                token_endpoint_auth_method = "none",
                metadata     = new { project_id = project.Id.ToString(), org_id = id.ToString() }
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
        return Created($"/admin/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpPatch("projects/{id}")]
    public async Task<IActionResult> AdminUpdateProject(Guid id, [FromBody] AdminUpdateProjectRequest body)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        if (body.Name != null) project.Name = body.Name;
        if (body.RequireRoleToLogin.HasValue)       project.RequireRoleToLogin       = body.RequireRoleToLogin.Value;
        if (body.RequireMfa.HasValue)               project.RequireMfa               = body.RequireMfa.Value;
        if (body.AllowSelfRegistration.HasValue)    project.AllowSelfRegistration    = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue)   project.SmsVerificationEnabled   = body.SmsVerificationEnabled.Value;
        if (body.Active.HasValue)                   project.Active                   = body.Active.Value;
        if (body.AllowedEmailDomains != null)       project.AllowedEmailDomains      = body.AllowedEmailDomains;
        var roleErr = await ApplyDefaultRoleAsync(project, body.ClearDefaultRole, body.DefaultRoleId, id);
        if (roleErr != null) return roleErr;
        ApplyLoginTheme(project, body.LoginTheme);
        if (body.IpAllowlist != null) project.IpAllowlist = body.IpAllowlist;
        if (body.CheckBreachedPasswords.HasValue) project.CheckBreachedPasswords = body.CheckBreachedPasswords.Value;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    private async Task<IActionResult?> ApplyDefaultRoleAsync(Project project, bool? clearRole, Guid? newRoleId, Guid projectId)
    {
        if (clearRole == true)
        {
            project.DefaultRoleId = null;
        }
        else if (newRoleId.HasValue)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == newRoleId && r.ProjectId == projectId);
            if (role == null) return BadRequest(new { error = "invalid_default_role" });
            project.DefaultRoleId = newRoleId;
        }
        return null;
    }

    private void ApplyLoginTheme(Project project, Dictionary<string, object>? theme)
    {
        if (theme == null) return;
        project.LoginTheme = TotpEncryption.EncryptProviderSecretsInTheme(theme, project.LoginTheme, appConfig.ThemeEncKey)!;
    }

    [HttpGet("projects/{id}/scopes")]
    public async Task<IActionResult> AdminGetProjectScopes(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        return Ok(new { custom_scopes = project.AllowedScopes, built_in = BuiltInScopes });
    }

    [HttpPut("projects/{id}/scopes")]
    public async Task<IActionResult> AdminUpdateProjectScopes(Guid id, [FromBody] UpdateScopesRequest body)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();

        var invalid = body.Scopes.Where(s => !System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-z0-9_:.-]+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))).ToArray();
        if (invalid.Length > 0) return BadRequest(new { error = "invalid_scope_names", invalid });

        project.AllowedScopes = body.Scopes;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        if (project.HydraClientId != null)
        {
            try { await hydra.UpdateOAuth2ClientScopeAsync(project.HydraClientId, project.AllowedScopes); }
            catch (Exception ex) { logger.LogWarning(ex, "Hydra scope update failed for project {ProjectId}", id); }
        }

        await audit.RecordAsync(null, id, GetActorId(), "project.scopes_updated", "project", id.ToString());
        return Ok(new { project.Id, custom_scopes = project.AllowedScopes });
    }

    [HttpDelete("projects/{id}")]
    public async Task<IActionResult> AdminDeleteProject(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        if (!string.IsNullOrEmpty(project.HydraClientId))
        {
            try { await hydra.DeleteOAuth2ClientAsync(project.HydraClientId); }
            catch (Exception ex) { logger.LogWarning(ex, "Hydra client deletion failed for {ClientId}", project.HydraClientId); }
        }
        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, id, GetActorId(), "project.deleted", "project", id.ToString());
        return NoContent();
    }

    [HttpPut("projects/{id}/userlist")]
    public async Task<IActionResult> AdminAssignUserList(Guid id, [FromBody] AdminAssignUserListRequest body)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == body.UserListId && ul.OrgId == project.OrgId);
        if (list == null) return BadRequest(new { error = "userlist_not_in_org" });
        project.AssignedUserListId = body.UserListId;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.AssignedUserListId });
    }

    [HttpDelete("projects/{id}/userlist")]
    public async Task<IActionResult> AdminUnassignUserList(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, message = "userlist_unassigned" });
    }

    [HttpGet("projects/{id}/stats")]
    public async Task<IActionResult> AdminGetProjectStats(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project?.AssignedUserListId == null) return NotFound();

        var totalUsers  = await db.Users.CountAsync(u => u.UserListId == project.AssignedUserListId);
        var activeUsers = await db.Users.CountAsync(u => u.UserListId == project.AssignedUserListId && u.Active);
        var usersByRole = await db.UserProjectRoles
            .Include(r => r.Role)
            .Where(r => r.ProjectId == id)
            .GroupBy(r => new { r.RoleId, r.Role.Name })
            .Select(g => new { role_id = g.Key.RoleId, role_name = g.Key.Name, count = g.Count() })
            .ToListAsync();

        return Ok(new { total_users = totalUsers, active_users = activeUsers, users_by_role = usersByRole });
    }

    // ── Roles (admin scope) ───────────────────────────────────────────────────

    [HttpGet("projects/{id}/roles")]
    public async Task<IActionResult> AdminListRoles(Guid id)
    {
var roles = await db.Roles
            .Where(r => r.ProjectId == id)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank })
            .ToListAsync();
        return Ok(roles);
    }

    [HttpPost("projects/{id}/roles")]
    public async Task<IActionResult> AdminCreateRole(Guid id, [FromBody] AdminCreateRoleRequest body)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == id)) return NotFound();
        var role = new Role
        {
            ProjectId = id, Name = body.Name, Description = body.Description,
            Rank = body.Rank ?? 100, CreatedBy = GetActorId(), CreatedAt = DateTimeOffset.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/admin/projects/{id}/roles/{role.Id}", new { role.Id, role.Name, role.Rank });
    }

    [HttpDelete("projects/{id}/roles/{rid}")]
    public async Task<IActionResult> AdminDeleteRole(Guid id, Guid rid)
    {
var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == rid && r.ProjectId == id);
        if (role == null) return NotFound();
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Email overview ────────────────────────────────────────────────────────

    [HttpGet("email/overview")]
    public async Task<IActionResult> GetEmailOverview()
    {
        var globalConfigured = !string.IsNullOrEmpty(appConfig.SmtpHost);

        var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .OrderBy(o => o.Name)
            .Select(o => new
            {
                o.Id,
                o.Name,
                o.Slug,
                SmtpConfig = db.OrgSmtpConfigs
                    .Where(c => c.OrgId == o.Id)
                    .Select(c => new { c.Host, c.Port, c.StartTls, c.FromAddress, c.FromName, c.UpdatedAt })
                    .FirstOrDefault(),
                ProjectOverrides = db.Projects
                    .Where(p => p.OrgId == o.Id && p.EmailFromName != null)
                    .Select(p => new { p.Id, p.Name, p.EmailFromName })
                    .ToList(),
            })
            .ToListAsync();

        return Ok(new
        {
            global_smtp = new
            {
                configured   = globalConfigured,
                host         = appConfig.SmtpHost,
                port         = appConfig.SmtpPort,
                start_tls    = appConfig.SmtpStartTls,
                from_address = appConfig.SmtpFromAddress,
                from_name    = appConfig.SmtpFromName,
            },
            orgs = orgs.Select(o => new
            {
                o.Id,
                o.Name,
                o.Slug,
                smtp_configured  = o.SmtpConfig != null,
                smtp_host        = o.SmtpConfig?.Host,
                smtp_port        = o.SmtpConfig?.Port,
                smtp_from_address = o.SmtpConfig?.FromAddress,
                smtp_from_name   = o.SmtpConfig?.FromName,
                smtp_updated_at  = o.SmtpConfig?.UpdatedAt,
                project_overrides = o.ProjectOverrides,
            }),
        });
    }

    // ── Org SMTP ──────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/smtp")]
    public async Task<IActionResult> GetOrgSmtp(Guid id)
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        if (config == null) return Ok(new { configured = false });
        return Ok(new
        {
            configured   = true,
            config.Host,
            config.Port,
            config.StartTls,
            config.Username,
            config.FromAddress,
            config.FromName,
            config.UpdatedAt,
        });
    }

    [HttpPut("organizations/{id}/smtp")]
    public async Task<IActionResult> UpsertOrgSmtp(Guid id, [FromBody] AdminUpsertSmtpRequest body)
    {
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        if (config == null)
        {
            config = new OrgSmtpConfig
            {
                OrgId       = id,
                Host        = body.Host,
                Port        = body.Port,
                StartTls    = body.StartTls,
                Username    = body.Username,
                PasswordEnc = body.Password != null
                    ? TotpEncryption.Encrypt(appConfig.SmtpEncKey, Encoding.UTF8.GetBytes(body.Password))
                    : null,
                FromAddress = body.FromAddress,
                FromName    = body.FromName,
                CreatedAt   = DateTimeOffset.UtcNow,
                UpdatedAt   = DateTimeOffset.UtcNow,
            };
            db.OrgSmtpConfigs.Add(config);
        }
        else
        {
            config.Host        = body.Host;
            config.Port        = body.Port;
            config.StartTls    = body.StartTls;
            config.Username    = body.Username;
            if (body.Password != null)
                config.PasswordEnc = TotpEncryption.Encrypt(appConfig.SmtpEncKey, Encoding.UTF8.GetBytes(body.Password));
            config.FromAddress = body.FromAddress;
            config.FromName    = body.FromName;
            config.UpdatedAt   = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "smtp_config_saved" });
    }

    [HttpDelete("organizations/{id}/smtp")]
    public async Task<IActionResult> DeleteOrgSmtp(Guid id)
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        if (config == null) return NoContent();
        db.OrgSmtpConfigs.Remove(config);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("organizations/{id}/smtp/test")]
    public async Task<IActionResult> TestOrgSmtp(Guid id)
    {
        var actor = await db.Users.FirstOrDefaultAsync(u => u.Id == Claims.ParsedUserId);
        if (actor == null) return BadRequest(new { error = "user_not_found" });
        try
        {
            await emailService.SendOtpAsync(actor.Email, "123456", "registration", id);
            return Ok(new { message = "test_email_sent", to = actor.Email });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP test failed for org {OrgId}", id);
            return BadRequest(new { error = "smtp_test_failed", detail = ex.Message });
        }
    }

    // ── Audit + Metrics ────────────────────────────────────────────────────────

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        limit  = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        var logs = await db.AuditLogs
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt, l.Metadata })
            .ToListAsync();
        return Ok(logs);
    }

    [HttpGet("metrics")]
    public async Task<IActionResult> GetMetrics()
    {
return Ok(new
        {
            org_count    = await db.Organisations.CountAsync(),
            active_users = await db.Users.CountAsync(u => u.Active),
            project_count = await db.Projects.CountAsync()
        });
    }

    [HttpGet("hydra/clients")]
    public async Task<IActionResult> ListHydraClients()
    {
var clients = await hydra.ListOAuth2ClientsAsync();
        return Ok(clients);
    }

    [HttpPost("hydra/clients")]
    public async Task<IActionResult> CreateHydraClient([FromBody] CreateHydraClientRequest body)
    {
var client = await hydra.CreateOAuth2ClientAsync(new
        {
            client_name = body.ClientName,
            grant_types = body.GrantTypes,
            redirect_uris = body.RedirectUris,
            scope = body.Scope ?? "openid profile offline_access",
            token_endpoint_auth_method = body.GrantTypes.Contains("client_credentials") ? "private_key_jwt" : "none",
        });
        return Ok(client);
    }

    [HttpGet("hydra/clients/{id}")]
    public async Task<IActionResult> GetHydraClient(string id)
    {
var client = await hydra.GetOAuth2ClientAsync(id);
        if (client == null) return NotFound();
        return Ok(client);
    }

    [HttpDelete("hydra/clients/{id}")]
    public async Task<IActionResult> DeleteHydraClient(string id)
    {
await hydra.DeleteOAuth2ClientAsync(id);
        return NoContent();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [HttpGet("organizations/{id}/export/users")]
    public async Task<IActionResult> AdminExportUsers(Guid id, [FromQuery] string format = "csv")
    {
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        var rateLimitKey = $"export_rl:{GetActorId()}:admin:users:{id}";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(id, null, GetActorId(), "export.users", AuditOrg, id.ToString(),
            new Dictionary<string, object> { ["format"] = format });

        var userListIds = await db.UserLists.Where(ul => ul.OrgId == id).Select(ul => ul.Id).ToListAsync();
        var users = db.Users
            .Where(u => userListIds.Contains(u.UserListId))
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Email, u.Username, u.DisplayName, u.Phone, u.Active, u.EmailVerified, u.TotpEnabled, u.UserListId, u.LastLoginAt, u.CreatedAt });

        if (format == "json")
        {
            var data = await users.ToListAsync();
            Response.Headers.ContentDisposition = $"attachment; filename=users-org-{id}.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = $"attachment; filename=users-org-{id}.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,email,username,display_name,phone,active,email_verified,totp_enabled,user_list_id,last_login_at,created_at\n");
        await foreach (var u in users.AsAsyncEnumerable())
            await Response.WriteAsync($"{u.Id},{AdminCsvEscape(u.Email)},{AdminCsvEscape(u.Username)},{AdminCsvEscape(u.DisplayName)},{AdminCsvEscape(u.Phone)},{u.Active},{u.EmailVerified},{u.TotpEnabled},{u.UserListId},{u.LastLoginAt:O},{u.CreatedAt:O}\n");
        return Empty;
    }

    [HttpGet("organizations/{id}/export/audit-log")]
    public async Task<IActionResult> AdminExportAuditLog(
        Guid id,
        [FromQuery] string format = "csv",
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null)
    {
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        var rateLimitKey = $"export_rl:{GetActorId()}:admin:auditlog:{id}";
        if (await cache.GetAsync(rateLimitKey) != null)
            return StatusCode(429, new { error = "export_rate_limited", retry_after_seconds = appConfig.ExportRateLimitMinutes * 60 });
        await cache.SetAsync(rateLimitKey, [1], new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(appConfig.ExportRateLimitMinutes) });

        await audit.RecordAsync(id, null, GetActorId(), "export.audit_log", AuditOrg, id.ToString(),
            new Dictionary<string, object> { ["format"] = format, ["from"] = from?.ToString("O") ?? "", ["to"] = to?.ToString("O") ?? "" });

        var query = db.AuditLogs
            .Where(l => l.OrgId == id)
            .Where(l => from == null || l.CreatedAt >= from)
            .Where(l => to == null || l.CreatedAt <= to)
            .OrderBy(l => l.CreatedAt)
            .Select(l => new { l.Id, l.Action, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt });

        if (format == "json")
        {
            var data = await query.ToListAsync();
            Response.Headers.ContentDisposition = $"attachment; filename=audit-log-{id}.json";
            return new JsonResult(data);
        }

        Response.Headers.ContentDisposition = $"attachment; filename=audit-log-{id}.csv";
        Response.ContentType = "text/csv";
        await Response.WriteAsync("id,action,project_id,actor_id,target_type,target_id,ip_address,created_at\n");
        await foreach (var l in query.AsAsyncEnumerable())
            await Response.WriteAsync($"{l.Id},{AdminCsvEscape(l.Action)},{l.ProjectId},{l.ActorId},{AdminCsvEscape(l.TargetType)},{AdminCsvEscape(l.TargetId)},{AdminCsvEscape(l.IpAddress)},{l.CreatedAt:O}\n");
        return Empty;
    }

    private static string AdminCsvEscape(string? value)
    {
        if (value == null) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ── SAML IdP management (admin) ───────────────────────────────────────────

    [HttpGet("projects/{id}/saml-providers")]
    public async Task<IActionResult> AdminListSamlProviders(Guid id)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        var providers = await db.SamlIdpConfigs
            .Where(x => x.ProjectId == id)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new {
                x.Id, x.EntityId, x.MetadataUrl, x.SsoUrl,
                x.EmailAttributeName, x.DisplayNameAttributeName,
                x.JitProvisioning, x.DefaultRoleId, x.Active,
                x.CreatedAt, x.UpdatedAt
            })
            .ToListAsync();
        return Ok(providers);
    }

    [HttpPost("projects/{id}/saml-providers")]
    public async Task<IActionResult> AdminCreateSamlProvider(Guid id, [FromBody] AdminCreateSamlProviderRequest req)
    {
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();

        var entity = new SamlIdpConfig
        {
            ProjectId                = id,
            EntityId                 = req.EntityId,
            MetadataUrl              = req.MetadataUrl,
            SsoUrl                   = req.SsoUrl,
            CertificatePem           = req.CertificatePem,
            EmailAttributeName       = req.EmailAttributeName ?? "email",
            DisplayNameAttributeName = req.DisplayNameAttributeName,
            JitProvisioning          = req.JitProvisioning ?? true,
            DefaultRoleId            = req.DefaultRoleId,
            Active                   = true,
            CreatedAt                = DateTimeOffset.UtcNow,
            UpdatedAt                = DateTimeOffset.UtcNow
        };
        db.SamlIdpConfigs.Add(entity);
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, id, GetActorId(), "saml_provider.created", "saml_idp_config", entity.Id.ToString());
        return Ok(new { entity.Id });
    }

    [HttpPatch("projects/{projectId}/saml-providers/{providerId}")]
    public async Task<IActionResult> AdminUpdateSamlProvider(Guid projectId, Guid providerId, [FromBody] AdminUpdateSamlProviderRequest req)
    {
        var provider = await db.SamlIdpConfigs
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == providerId && x.ProjectId == projectId);
        if (provider == null) return NotFound();

        if (req.EntityId != null)                 provider.EntityId                 = req.EntityId;
        if (req.MetadataUrl != null)               provider.MetadataUrl               = req.MetadataUrl;
        if (req.SsoUrl != null)                    provider.SsoUrl                    = req.SsoUrl;
        if (req.CertificatePem != null)            provider.CertificatePem            = req.CertificatePem;
        if (req.EmailAttributeName != null)        provider.EmailAttributeName        = req.EmailAttributeName;
        if (req.DisplayNameAttributeName != null)  provider.DisplayNameAttributeName  = req.DisplayNameAttributeName;
        if (req.JitProvisioning.HasValue)          provider.JitProvisioning           = req.JitProvisioning.Value;
        if (req.DefaultRoleId.HasValue)            provider.DefaultRoleId             = req.DefaultRoleId == Guid.Empty ? null : req.DefaultRoleId;
        if (req.Active.HasValue)                   provider.Active                    = req.Active.Value;
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync();
        await audit.RecordAsync(provider.Project.OrgId, projectId, GetActorId(), "saml_provider.updated", "saml_idp_config", providerId.ToString());
        return Ok();
    }

    [HttpDelete("projects/{projectId}/saml-providers/{providerId}")]
    public async Task<IActionResult> AdminDeleteSamlProvider(Guid projectId, Guid providerId)
    {
        var provider = await db.SamlIdpConfigs
            .Include(x => x.Project)
            .FirstOrDefaultAsync(x => x.Id == providerId && x.ProjectId == projectId);
        if (provider == null) return NotFound();

        db.SamlIdpConfigs.Remove(provider);
        await db.SaveChangesAsync();
        await audit.RecordAsync(provider.Project.OrgId, projectId, GetActorId(), "saml_provider.deleted", "saml_idp_config", providerId.ToString());
        return NoContent();
    }

}

// ── Request records ───────────────────────────────────────────────────────────
public record CreateOrgRequest(string Name, string Slug);
public record UpdateOrgRequest(string? Name, int? AuditRetentionDays);
public record AdminCreateUserRequest(string Email, string? Password, string? Username, bool? EmailVerified);
public record AssignOrgAdminRequest([property: System.Text.Json.Serialization.JsonRequired] Guid UserId, string Role, Guid? ScopeId);
public record AdminCreateUserListRequest(string Name, [property: System.Text.Json.Serialization.JsonRequired] Guid OrgId);
public record AdminCreateProjectRequest(string Name, string Slug, bool? RequireRoleToLogin, string[]? RedirectUris);
public record AdminUpdateProjectRequest(string? Name, bool? RequireRoleToLogin, bool? RequireMfa, bool? AllowSelfRegistration, bool? EmailVerificationEnabled,
    bool? SmsVerificationEnabled, bool? Active, Guid? DefaultRoleId, bool? ClearDefaultRole, string[]? AllowedEmailDomains, Dictionary<string, object>? LoginTheme,
    string[]? IpAllowlist, bool? CheckBreachedPasswords);
public record AdminAssignUserListRequest([property: System.Text.Json.Serialization.JsonRequired] Guid UserListId);
public record AdminCreateRoleRequest(string Name, string? Description, int? Rank);
public record CreateHydraClientRequest(string ClientName, string[] GrantTypes, string[] RedirectUris, string? Scope);
public record AdminUpsertSmtpRequest(string Host, [property: System.Text.Json.Serialization.JsonRequired] int Port, [property: System.Text.Json.Serialization.JsonRequired] bool StartTls, string? Username, string? Password, string FromAddress, string FromName);
public record AdminCreateSamlProviderRequest(string EntityId, string? MetadataUrl, string? SsoUrl, string? CertificatePem, string? EmailAttributeName, string? DisplayNameAttributeName, bool? JitProvisioning, Guid? DefaultRoleId);
public record AdminUpdateSamlProviderRequest(string? EntityId, string? MetadataUrl, string? SsoUrl, string? CertificatePem, string? EmailAttributeName, string? DisplayNameAttributeName, bool? JitProvisioning, Guid? DefaultRoleId, bool? Active);
