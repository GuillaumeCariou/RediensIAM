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
    PatGenerationService patGen,
    ImpersonationService impersonation,
    IConfiguration config,
    ILogger<AdminController> logger) : ControllerBase
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
        var org = await db.Organisations
            .Where(o => o.Id == id)
            .Select(o => new {
                o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt, o.UpdatedAt,
                o.OrgListId, o.CreatedBy
            })
            .FirstOrDefaultAsync();
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

    [HttpPost("/admin/organisations/{id}/unsuspend")]
    public async Task<IActionResult> UnsuspendOrg(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = true;
        org.SuspendedAt = null;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.unsuspended", "organisation", id.ToString());
        return Ok(new { message = "org_unsuspended" });
    }

    [HttpDelete("/admin/organisations/{id}")]
    public async Task<IActionResult> DeleteOrg(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();

        // 1. Clean up projects: revoke Hydra clients, delete Keto tuples
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

        // 2. Clean up org roles
        var orgRoles = await db.OrgRoles.Where(r => r.OrgId == id).ToListAsync();
        db.OrgRoles.RemoveRange(orgRoles);

        // 3. Clean up user lists: remove users and their Keto member tuples
        var lists = await db.UserLists.Where(ul => ul.OrgId == id).ToListAsync();
        foreach (var list in lists)
        {
            var users = await db.Users.Where(u => u.UserListId == list.Id).ToListAsync();
            foreach (var u in users)
                await keto.DeleteRelationTupleAsync("UserLists", list.Id.ToString(), "member", $"user:{u.Id}");
            db.Users.RemoveRange(users);
        }
        db.UserLists.RemoveRange(lists);

        // 4. Delete org Keto tuple
        await keto.DeleteRelationTupleAsync("Organisations", id.ToString(), "org", "System:rediensiam");

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
            .Include(u => u.UserList)
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.Active, u.UserListId, u.LastLoginAt, OrgId = u.UserList.OrgId })
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

    [HttpPost("/admin/users/{id}/enable")]
    public async Task<IActionResult> EnableUser(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();
        user.Active = true;
        user.DisabledAt = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { message = "user_enabled" });
    }

    [HttpPost("/admin/users/{id}/impersonate")]
    public async Task<IActionResult> ImpersonateUser(Guid id, [FromBody] ImpersonateRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var actorId = GetActorId();
        if (actorId == id) return BadRequest(new { error = "cannot_impersonate_self" });

        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();

        var project = await db.Projects.FindAsync(body.ProjectId);
        if (project == null) return BadRequest(new { error = "project_not_found" });

        // Verify user is in this project's userlist
        var inList = project.AssignedUserListId.HasValue &&
            await db.Users.AnyAsync(u => u.Id == id && u.UserListId == project.AssignedUserListId);
        if (!inList) return BadRequest(new { error = "user_not_in_project" });

        // Collect user's roles in this project
        var roles = await db.UserProjectRoles
            .Where(r => r.UserId == id && r.ProjectId == body.ProjectId)
            .Include(r => r.Role)
            .Select(r => r.Role.Name)
            .ToListAsync();

        var claims = new ImpersonationClaims(
            UserId: $"{project.OrgId}:{id}",
            OrgId: project.OrgId.ToString(),
            ProjectId: body.ProjectId.ToString(),
            Roles: roles,
            ImpersonatedBy: actorId.ToString());

        var token = await impersonation.CreateAsync(claims);
        await audit.RecordAsync(project.OrgId, body.ProjectId, actorId,
            "admin.impersonate", "user", id.ToString());

        return Ok(new
        {
            token,
            expires_in_minutes = 15,
            user_id = id,
            project_id = body.ProjectId,
            warning = "This token grants access as the target user. Handle with extreme care."
        });
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
            .Select(ul => new {
                ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt,
                OrgName = ul.Organisation != null ? ul.Organisation.Name : null
            }).ToListAsync();
        return Ok(lists);
    }

    [HttpGet("/admin/userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var ul = await db.UserLists.Include(ul => ul.Organisation).FirstOrDefaultAsync(ul => ul.Id == id);
        if (ul == null) return NotFound();
        return Ok(new
        {
            ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt,
            org_name = ul.Organisation?.Name,
            user_count = await db.Users.CountAsync(u => u.UserListId == id)
        });
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
        var username = body.Username ?? body.Email.Split('@')[0];
        string discriminator;
        do { discriminator = Random.Shared.Next(1000, 9999).ToString(); }
        while (await db.Users.AnyAsync(u => u.UserListId == id && u.Username == username && u.Discriminator == discriminator));
        var user = new User
        {
            UserListId = id, Username = username,
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
            .Where(sa => sa.IsSystem)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt }).ToListAsync();
        return Ok(sas);
    }

    [HttpPost("/admin/service-accounts")]
    public async Task<IActionResult> CreateSystemServiceAccount([FromBody] CreateSystemSaRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var rootList = await GetOrCreateRootListAsync();
        var sa = new ServiceAccount
        {
            UserListId = rootList.Id,
            Name = body.Name,
            Description = body.Description,
            IsSystem = true,
            Active = true,
            CreatedBy = GetActorId(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccounts.Add(sa);
        await db.SaveChangesAsync();
        await audit.RecordAsync(null, null, GetActorId(), "sa.created", "service_account", sa.Id.ToString());
        return Created($"/admin/service-accounts/{sa.Id}", new { sa.Id, sa.Name, sa.Description });
    }

    [HttpGet("/admin/service-accounts/{id}")]
    public async Task<IActionResult> GetSystemServiceAccount(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts
            .Include(sa => sa.PersonalAccessTokens)
            .Include(sa => sa.OrgRoles)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        return Ok(new
        {
            sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt,
            pats = sa.PersonalAccessTokens.Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt }),
            roles = sa.OrgRoles.Select(r => new { r.Id, r.Role, r.GrantedAt })
        });
    }

    [HttpDelete("/admin/service-accounts/{id}")]
    public async Task<IActionResult> DeleteSystemServiceAccount(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts
            .Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        db.ServiceAccounts.Remove(sa);
        await db.SaveChangesAsync();
        await audit.RecordAsync(null, null, GetActorId(), "sa.deleted", "service_account", id.ToString());
        return NoContent();
    }

    [HttpPost("/admin/service-accounts/{id}/pat")]
    public async Task<IActionResult> GenerateSystemPat(Guid id, [FromBody] GeneratePatRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts
            .Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        var (raw, pat) = await patGen.GenerateAsync(id, body.Name, body.ExpiresAt, GetActorId());
        return Created($"/admin/service-accounts/{id}/pat/{pat.Id}", new
        {
            pat.Id, pat.Name, pat.ExpiresAt,
            token = raw
        });
    }

    [HttpGet("/admin/service-accounts/{id}/pat")]
    public async Task<IActionResult> ListSystemPats(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var pats = await db.PersonalAccessTokens
            .Where(p => p.ServiceAccountId == id)
            .Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt })
            .ToListAsync();
        return Ok(pats);
    }

    [HttpDelete("/admin/service-accounts/{id}/pat/{patId}")]
    public async Task<IActionResult> RevokeSystemPat(Guid id, Guid patId)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var pat = await db.PersonalAccessTokens.FirstOrDefaultAsync(p => p.Id == patId && p.ServiceAccountId == id);
        if (pat == null) return NotFound();
        db.PersonalAccessTokens.Remove(pat);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("/admin/service-accounts/{id}/roles")]
    public async Task<IActionResult> ListSystemSaRoles(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var roles = await db.ServiceAccountOrgRoles
            .Where(r => r.ServiceAccountId == id)
            .Select(r => new { r.Id, r.Role, r.GrantedAt })
            .ToListAsync();
        return Ok(roles);
    }

    [HttpPost("/admin/service-accounts/{id}/roles")]
    public async Task<IActionResult> AssignSystemSaRole(Guid id, [FromBody] AssignSystemSaRoleRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts
            .Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        var existing = await db.ServiceAccountOrgRoles.FirstOrDefaultAsync(r => r.ServiceAccountId == id && r.Role == body.Role);
        if (existing != null) return Ok(new { existing.Id, existing.Role, existing.GrantedAt });
        var role = new ServiceAccountOrgRole
        {
            ServiceAccountId = id,
            Role = body.Role,
            GrantedBy = GetActorId(),
            GrantedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccountOrgRoles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/admin/service-accounts/{id}/roles/{role.Id}", new { role.Id, role.Role, role.GrantedAt });
    }

    [HttpDelete("/admin/service-accounts/{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveSystemSaRole(Guid id, Guid roleId)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var role = await db.ServiceAccountOrgRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.ServiceAccountId == id);
        if (role == null) return NotFound();
        db.ServiceAccountOrgRoles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── JWT Profile keys (system SAs) ─────────────────────────────────────────

    [HttpGet("/admin/service-accounts/{id}/keys")]
    public async Task<IActionResult> GetSystemSaKeys(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        if (sa.HydraClientId == null) return Ok(new { client_id = (string?)null, has_key = false });
        var client = await hydra.GetOAuth2ClientAsync(sa.HydraClientId);
        if (client is null) return Ok(new { client_id = sa.HydraClientId, has_key = false });
        var hasJwks = client.Value.TryGetProperty("jwks", out var jwks)
            && jwks.TryGetProperty("keys", out var keys) && keys.GetArrayLength() > 0;
        var kid = hasJwks && jwks.TryGetProperty("keys", out var ks) && ks.GetArrayLength() > 0
            ? ks[0].TryGetProperty("kid", out var k) ? k.GetString() : null : null;
        return Ok(new { client_id = sa.HydraClientId, has_key = hasJwks, kid });
    }

    [HttpPost("/admin/service-accounts/{id}/keys")]
    public async Task<IActionResult> AddSystemSaKey(Guid id, [FromBody] SaKeyRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        var clientId = $"sa_{id}";
        try { await hydra.CreateOrUpdateServiceAccountClientAsync(clientId, sa.Name, body.Jwk); }
        catch (Exception ex) { return BadRequest(new { error = "hydra_error", detail = ex.Message }); }
        sa.HydraClientId = clientId;
        await db.SaveChangesAsync();
        return Ok(new { client_id = clientId });
    }

    [HttpDelete("/admin/service-accounts/{id}/keys")]
    public async Task<IActionResult> RemoveSystemSaKey(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        if (sa.HydraClientId != null)
        {
            await hydra.DeleteOAuth2ClientAsync(sa.HydraClientId);
            sa.HydraClientId = null;
            await db.SaveChangesAsync();
        }
        return Ok(new { message = "key_removed" });
    }

    private async Task<UserList> GetOrCreateRootListAsync()
    {
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.OrgId == null && ul.Immovable);
        if (list != null) return list;
        list = new UserList { Name = "__system__", OrgId = null, Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    // ── Audit + Metrics ────────────────────────────────────────────────────────

    [HttpGet("/admin/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var logs = await db.AuditLogs
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new {
                l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId,
                l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt
            })
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

    // ── Org Admins ────────────────────────────────────────────────────────────

    [HttpGet("/admin/organisations/{id}/admins")]
    public async Task<IActionResult> ListOrgAdmins(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var roles = await db.OrgRoles
            .Where(r => r.OrgId == id)
            .Include(r => r.User)
            .ToListAsync();
        var projectIds = roles.Where(r => r.ScopeId.HasValue).Select(r => r.ScopeId!.Value).Distinct().ToList();
        var projects = await db.Projects
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
        return Ok(roles.Select(r => new
        {
            r.Id, r.OrgId, r.UserId, r.Role, r.ScopeId, r.GrantedAt,
            user_name = $"{r.User.Username}#{r.User.Discriminator}",
            user_email = r.User.Email,
            scope_name = r.ScopeId.HasValue && projects.TryGetValue(r.ScopeId.Value, out var p) ? p.Name : null
        }));
    }

    [HttpPost("/admin/organisations/{id}/admins")]
    public async Task<IActionResult> AssignOrgAdmin(Guid id, [FromBody] AssignOrgAdminRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var existing = await db.OrgRoles.FirstOrDefaultAsync(r =>
            r.OrgId == id && r.UserId == body.UserId && r.Role == body.Role && r.ScopeId == body.ScopeId);
        if (existing != null)
            return Ok(new { existing.Id });
        var role = new OrgRole
        {
            OrgId = id, UserId = body.UserId, Role = body.Role,
            ScopeId = body.ScopeId, GrantedBy = GetActorId(), GrantedAt = DateTimeOffset.UtcNow
        };
        db.OrgRoles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/admin/organisations/{id}/admins/{role.Id}", new { role.Id });
    }

    [HttpDelete("/admin/organisations/{id}/admins/{roleId}")]
    public async Task<IActionResult> RemoveOrgAdmin(Guid id, Guid roleId)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var role = await db.OrgRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.OrgId == id);
        if (role == null) return NotFound();
        db.OrgRoles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Org Service Accounts ──────────────────────────────────────────────────

    [HttpGet("/admin/organisations/{id}/service-accounts")]
    public async Task<IActionResult> ListOrgServiceAccounts(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var sas = await db.ServiceAccounts
            .Where(sa => sa.UserList.OrgId == id)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt })
            .ToListAsync();
        return Ok(sas);
    }

    // ── UserList creation (admin scope) ───────────────────────────────────────

    [HttpPost("/admin/userlists")]
    public async Task<IActionResult> AdminCreateUserList([FromBody] AdminCreateUserListRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var ul = new UserList
        {
            Name = body.Name, OrgId = body.OrgId, Immovable = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/admin/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    // ── Projects (admin scope) ────────────────────────────────────────────────

    [HttpGet("/admin/organisations/{id}/projects")]
    public async Task<IActionResult> AdminListOrgProjects(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var projects = await db.Projects
            .Where(p => p.OrgId == id)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active })
            .ToListAsync();
        return Ok(projects);
    }

    [HttpPost("/admin/organisations/{id}/projects")]
    public async Task<IActionResult> AdminCreateProject(Guid id, [FromBody] AdminCreateProjectRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var actorId = GetActorId();
        var project = new Project
        {
            OrgId = id, Name = body.Name, Slug = body.Slug,
            RequireRoleToLogin = body.RequireRoleToLogin,
            Active = true, CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        try
        {
            await hydra.CreateOAuth2ClientAsync(new
            {
                client_id = $"client_{project.Id}",
                client_name = $"Project: {project.Name}",
                redirect_uris = body.RedirectUris ?? [],
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                scope = "openid profile offline_access",
                token_endpoint_auth_method = "none",
                metadata = new { project_id = project.Id.ToString(), org_id = id.ToString() }
            });
            project.HydraClientId = $"client_{project.Id}";
            await db.SaveChangesAsync();
        }
        catch (Exception ex) { logger.LogWarning(ex, "Hydra client creation failed for project {ProjectId}", project.Id); }
        await keto.WriteRelationTupleAsync("Projects", project.Id.ToString(), "org", $"Organisations:{id}");
        await audit.RecordAsync(id, project.Id, actorId, "project.created", "project", project.Id.ToString());
        return Created($"/admin/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpDelete("/admin/projects/{id}")]
    public async Task<IActionResult> AdminDeleteProject(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
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

    [HttpGet("/admin/projects/{id}")]
    public async Task<IActionResult> AdminGetProject(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id);
        if (project == null) return NotFound();
        return Ok(new
        {
            project.Id, project.Name, project.Slug, project.OrgId, project.Active,
            project.HydraClientId, project.AssignedUserListId,
            project.RequireRoleToLogin, project.AllowSelfRegistration,
            project.EmailVerificationEnabled, project.SmsVerificationEnabled,
            project.CreatedAt, project.UpdatedAt
        });
    }

    [HttpPatch("/admin/projects/{id}")]
    public async Task<IActionResult> AdminUpdateProject(Guid id, [FromBody] AdminUpdateProjectRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        if (body.Name != null) project.Name = body.Name;
        if (body.RequireRoleToLogin.HasValue) project.RequireRoleToLogin = body.RequireRoleToLogin.Value;
        if (body.AllowSelfRegistration.HasValue) project.AllowSelfRegistration = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue) project.SmsVerificationEnabled = body.SmsVerificationEnabled.Value;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    [HttpPost("/admin/projects/{id}/assign-userlist")]
    public async Task<IActionResult> AdminAssignUserList(Guid id, [FromBody] AdminAssignUserListRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == body.UserListId && ul.OrgId == project.OrgId);
        if (list == null) return BadRequest(new { error = "userlist_not_in_org" });
        project.AssignedUserListId = body.UserListId;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.AssignedUserListId });
    }

    [HttpDelete("/admin/projects/{id}/assign-userlist")]
    public async Task<IActionResult> AdminUnassignUserList(Guid id)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, message = "userlist_unassigned" });
    }

    // ── Roles (admin scope) ───────────────────────────────────────────────────

    [HttpGet("/admin/projects/{id}/roles")]
    public async Task<IActionResult> AdminListRoles(Guid id)
    {
        if (!HasAdminAccess) return StatusCode(403);
        var roles = await db.Roles
            .Where(r => r.ProjectId == id)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank })
            .ToListAsync();
        return Ok(roles);
    }

    [HttpPost("/admin/projects/{id}/roles")]
    public async Task<IActionResult> AdminCreateRole(Guid id, [FromBody] AdminCreateRoleRequest body)
    {
        if (!IsSuperAdmin) return StatusCode(403);
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

    [HttpDelete("/admin/projects/{id}/roles/{rid}")]
    public async Task<IActionResult> AdminDeleteRole(Guid id, Guid rid)
    {
        if (!IsSuperAdmin) return StatusCode(403);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == rid && r.ProjectId == id);
        if (role == null) return NotFound();
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("/health")]
    [AllowAnonymousAttribute]
    public IActionResult Health() => Ok(new { status = "healthy" });

    private Guid GetActorId() => Claims?.ParsedUserId ?? Guid.Empty;
}

public record ImpersonateRequest(Guid ProjectId);
public record CreateOrgRequest(string Name, string Slug);
public record UpdateOrgRequest(string? Name);
public record AdminCreateUserRequest(string Email, string Password, string? Username);
public record AssignOrgAdminRequest(Guid UserId, string Role, Guid? ScopeId);
public record AdminCreateUserListRequest(string Name, Guid OrgId);
public record AdminCreateProjectRequest(string Name, string Slug, bool RequireRoleToLogin, string[]? RedirectUris);
public record AdminUpdateProjectRequest(string? Name, bool? RequireRoleToLogin, bool? AllowSelfRegistration, bool? EmailVerificationEnabled, bool? SmsVerificationEnabled);
public record AdminAssignUserListRequest(Guid UserListId);
public record CreateSystemSaRequest(string Name, string? Description);
public record AssignSystemSaRoleRequest(string Role);
public record AdminCreateRoleRequest(string Name, string? Description, int? Rank);
