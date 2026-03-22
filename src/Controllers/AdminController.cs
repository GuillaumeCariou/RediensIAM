using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class AdminController(
    RediensIamDbContext db,
    HydraAdminService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    IConfiguration config) : ControllerBase
{
    [HttpGet("/admin/config")]
    public IActionResult GetConfig()
    {
        var appUrl = config["App:PublicUrl"] ?? "http://localhost";
        return Ok(new
        {
            hydra_url = appUrl,
            client_id = "client_admin_system",
            redirect_uri = $"{appUrl}/admin/callback"
        });
    }

    private TokenClaims? Claims => HttpContext.GetClaims();
    private bool IsSuperAdmin => Claims?.Roles.Contains("super_admin") ?? false;
    private bool IsOrgAdmin => Claims?.Roles.Contains("org_admin") ?? false;
    private bool HasAdminAccess => IsSuperAdmin || IsOrgAdmin;

    // ── Organisations ─────────────────────────────────────────────────────────

    [HttpGet("/admin/organisations")]
    public async Task<IActionResult> ListOrgs()
    {
        if (!HasAdminAccess) return StatusCode(403);
        var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt }).ToListAsync();
        return Ok(orgs);
    }

    [HttpPost("/admin/organisations")]
    public async Task<IActionResult> CreateOrg([FromBody] CreateOrgRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var actorId = GetActorId();

        // 1. Create the immovable org list first
        var orgList = new UserList { Name = $"{body.Name} Org List", Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(orgList);
        await db.SaveChangesAsync();

        // 2. Create the org pointing to the list
        var org = new Organisation
        {
            Name = body.Name, Slug = body.Slug, OrgListId = orgList.Id,
            Active = true, CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Organisations.Add(org);
        await db.SaveChangesAsync();

        // 3. Set the list's OrgId back
        orgList.OrgId = org.Id;
        await db.SaveChangesAsync();

        await keto.WriteRelationTupleAsync("Organisations", org.Id.ToString(), "org", "System:rediensiam");
        await audit.RecordAsync(org.Id, null, actorId, "org.created", "organisation", org.Id.ToString());
        return Created($"/admin/organisations/{org.Id}", new { org.Id, org.Name, org.Slug, org_list_id = orgList.Id });
    }

    [HttpGet("/admin/organisations/{id}")]
    public async Task<IActionResult> GetOrg(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var org = await db.Organisations.Include(o => o.OrgList).FirstOrDefaultAsync(o => o.Id == id);
        if (org == null) return NotFound();
        return Ok(org);
    }

    [HttpPatch("/admin/organisations/{id}")]
    public async Task<IActionResult> UpdateOrg(Guid id, [FromBody] UpdateOrgRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        if (body.Name != null) org.Name = body.Name;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { org.Id, org.Name });
    }

    [HttpPost("/admin/organisations/{id}/suspend")]
    public async Task<IActionResult> SuspendOrg(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = false;
        org.SuspendedAt = DateTimeOffset.UtcNow;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.suspended", "organisation", id.ToString());
        return Ok(new { message = "org_suspended" });
    }

    [HttpDelete("/admin/organisations/{id}")]
    public async Task<IActionResult> DeleteOrg(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        db.Organisations.Remove(org);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("/admin/users")]
    public async Task<IActionResult> SearchUsers([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var query = db.Users.AsQueryable();
        if (!string.IsNullOrEmpty(q))
            query = query.Where(u => u.Email.Contains(q) || u.Username.Contains(q));
        var users = await query
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.Active, u.UserListId, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [HttpGet("/admin/users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var user = await db.Users.Include(u => u.UserList).ThenInclude(ul => ul.Organisation).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        return Ok(user);
    }

    [HttpPost("/admin/users/{id}/force-logout")]
    public async Task<IActionResult> ForceLogout(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgId = user.UserList.OrgId?.ToString() ?? "";
        await hydra.RevokeSessionsAsync($"{orgId}:{id}");
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.force_logout", "user", id.ToString());
        return Ok(new { message = "sessions_revoked" });
    }

    [HttpPost("/admin/users/{id}/disable")]
    public async Task<IActionResult> DisableUser(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.Active = false;
        user.DisabledAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "user_disabled" });
    }

    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("/admin/userlists")]
    public async Task<IActionResult> ListAllUserLists([FromQuery] Guid? org_id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var query = db.UserLists.AsQueryable();
        if (org_id.HasValue)
            query = query.Where(ul => ul.OrgId == org_id);
        var lists = await query
            .Select(ul => new { ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt }).ToListAsync();
        return Ok(lists);
    }

    [HttpGet("/admin/userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id)) return NotFound();
        var users = await db.Users
            .Where(u => u.UserListId == id)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [HttpDelete("/admin/userlists/{id}/users/{uid}")]
    public async Task<IActionResult> RemoveUserFromList(Guid id, Guid uid)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id);
        if (user == null) return NotFound();
        await keto.DeleteRelationTupleAsync("UserLists", id.ToString(), "member", $"user:{uid}");
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("/admin/userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] CreateUserRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var ul = await db.UserLists.FindAsync(id);
        if (ul == null) return NotFound();
        var discriminator = Random.Shared.Next(1000, 9999).ToString();
        var user = new User
        {
            UserListId = id, Username = body.Username ?? body.Email.Split('@')[0],
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash = passwords.Hash(body.Password),
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync("UserLists", id.ToString(), "member", $"user:{user.Id}");
        return Created($"/admin/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email
        });
    }

    // ── Service Accounts (system level) ───────────────────────────────────────

    [HttpGet("/admin/service-accounts")]
    public async Task<IActionResult> ListServiceAccounts()
    {
        if (!HasAdminAccess) return StatusCode(403);
        var sas = await db.ServiceAccounts
            .Where(sa => sa.UserList.OrgId == null)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active }).ToListAsync();
        return Ok(sas);
    }

    // ── Audit + Metrics ────────────────────────────────────────────────────────

    [HttpGet("/admin/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var logs = await db.AuditLogs
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        return Ok(logs);
    }

    [HttpGet("/admin/metrics")]
    public async Task<IActionResult> GetMetrics()
    {
        if (!HasAdminAccess) return StatusCode(403);
        var orgCount = await db.Organisations.CountAsync();
        var activeUsers = await db.Users.CountAsync(u => u.Active);
        var projectCount = await db.Projects.CountAsync();
        return Ok(new { org_count = orgCount, active_users = activeUsers, project_count = projectCount });
    }

    [HttpGet("/admin/hydra/clients")]
    public async Task<IActionResult> ListHydraClients()
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var clients = await db.Projects.Select(p => new { p.HydraClientId, p.Name, p.OrgId }).ToListAsync();
        return Ok(clients);
    }

    [HttpDelete("/admin/hydra/clients/{id}")]
    public async Task<IActionResult> DeleteHydraClient(string id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        await hydra.DeleteOAuth2ClientAsync(id);
        return NoContent();
    }

    [HttpGet("/health")]
    [AllowAnonymousAttribute]
    public IActionResult Health() => Ok(new { status = "healthy" });

    private Guid GetActorId()
    {
        var id = Claims?.UserId ?? "";
        return Guid.TryParse(id.Contains(':') ? id.Split(':')[1] : id, out var g) ? g : Guid.Empty;
    }
}

public record CreateOrgRequest(string Name, string Slug);
public record UpdateOrgRequest(string? Name);
public record AdminCreateUserRequest(string Email, string Password, string? Username);
