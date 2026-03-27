using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[RequireManagementLevel(ManagementLevel.OrgAdmin)]
public class SystemAdminController(
    RediensIamDbContext db,
    HydraAdminService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    PatGenerationService patGen,
    ImpersonationService impersonation,
    ServiceAccountService saService,
    RoleAssignmentService roleService,
    AppConfig appConfig,
    ILogger<SystemAdminController> logger) : ControllerBase
{
    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid GetActorId() => Claims.ParsedUserId;

    // ── Organisations ─────────────────────────────────────────────────────────

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/organisations")]
    public async Task<IActionResult> ListOrgs()
    {
var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt }).ToListAsync();
        return Ok(orgs);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/organisations")]
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
        return Created($"/admin/organisations/{org.Id}", new { org.Id, org.Name, org.Slug, org_list_id = orgList.Id });
    }

    [HttpGet("/admin/organisations/{id}")]
    public async Task<IActionResult> GetOrg(Guid id)
    {
var org = await db.Organisations
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt, o.UpdatedAt, o.OrgListId, o.CreatedBy })
            .FirstOrDefaultAsync();
        if (org == null) return NotFound();
        return Ok(org);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPatch("/admin/organisations/{id}")]
    public async Task<IActionResult> UpdateOrg(Guid id, [FromBody] UpdateOrgRequest body)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        if (body.Name != null) org.Name = body.Name;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { org.Id, org.Name });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/organisations/{id}/suspend")]
    public async Task<IActionResult> SuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = false; org.SuspendedAt = DateTimeOffset.UtcNow; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.suspended", "organisation", id.ToString());
        return Ok(new { message = "org_suspended" });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/organisations/{id}/unsuspend")]
    public async Task<IActionResult> UnsuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = true; org.SuspendedAt = null; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.unsuspended", "organisation", id.ToString());
        return Ok(new { message = "org_unsuspended" });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/organisations/{id}")]
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

        var lists = await db.UserLists.Where(ul => ul.OrgId == id).ToListAsync();
        foreach (var list in lists)
        {
            var users = await db.Users.Where(u => u.UserListId == list.Id).ToListAsync();
            foreach (var u in users)
                await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, list.Id.ToString(), "member", $"user:{u.Id}");
            db.Users.RemoveRange(users);
        }
        db.UserLists.RemoveRange(lists);

        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, id.ToString(), "org", $"{Roles.KetoSystemNamespace}:{Roles.KetoSystemObject}");

        db.Organisations.Remove(org);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/users")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/users/{id}")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPatch("/admin/users/{id}")]
    public async Task<IActionResult> UpdateUser(Guid id, [FromBody] AdminUpdateUserRequest body)
    {
var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        if (body.Email != null) { user.Email = body.Email.ToLowerInvariant(); user.EmailVerified = false; user.EmailVerifiedAt = null; }
        if (body.Username != null) user.Username = body.Username;
        if (body.DisplayName != null) user.DisplayName = body.DisplayName == "" ? null : body.DisplayName;
        if (body.Phone != null) user.Phone = body.Phone == "" ? null : body.Phone;
        if (body.Active.HasValue) { user.Active = body.Active.Value; user.DisabledAt = body.Active.Value ? null : DateTimeOffset.UtcNow; }
        if (body.EmailVerified.HasValue) { user.EmailVerified = body.EmailVerified.Value; user.EmailVerifiedAt = body.EmailVerified.Value ? DateTimeOffset.UtcNow : null; }
        if (body.ClearLock == true) { user.LockedUntil = null; user.FailedLoginCount = 0; }
        if (!string.IsNullOrEmpty(body.NewPassword)) user.PasswordHash = passwords.Hash(body.NewPassword);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.updated", "user", id.ToString());
        return Ok(new { user.Id, user.Email, user.Username, user.Discriminator, user.DisplayName, user.Phone, user.Active, user.EmailVerified, user.LockedUntil, user.FailedLoginCount });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/users/{id}/force-logout")]
    public async Task<IActionResult> ForceLogout(Guid id)
    {
var user = await db.Users.Include(u => u.UserList).FirstOrDefaultAsync(u => u.Id == id);
        if (user == null) return NotFound();
        var orgId = user.UserList.OrgId?.ToString() ?? "";
        await hydra.RevokeSessionsAsync($"{orgId}:{id}");
        await audit.RecordAsync(user.UserList.OrgId, null, GetActorId(), "user.force_logout", "user", id.ToString());
        return Ok(new { message = "sessions_revoked" });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/users/{id}/impersonate")]
    public async Task<IActionResult> ImpersonateUser(Guid id, [FromBody] ImpersonateRequest body)
    {
var actorId = GetActorId();
        if (actorId == id) return BadRequest(new { error = "cannot_impersonate_self" });

        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();

        var project = await db.Projects.FindAsync(body.ProjectId);
        if (project == null) return BadRequest(new { error = "project_not_found" });

        var inList = project.AssignedUserListId.HasValue &&
            await db.Users.AnyAsync(u => u.Id == id && u.UserListId == project.AssignedUserListId);
        if (!inList) return BadRequest(new { error = "user_not_in_project" });

        var roles = await db.UserProjectRoles
            .Where(r => r.UserId == id && r.ProjectId == body.ProjectId)
            .Include(r => r.Role)
            .Select(r => r.Role.Name)
            .ToListAsync();

        var impClaims = new ImpersonationClaims(
            UserId: $"{project.OrgId}:{id}",
            OrgId: project.OrgId.ToString(),
            ProjectId: body.ProjectId.ToString(),
            Roles: roles,
            ImpersonatedBy: actorId.ToString());

        var token = await impersonation.CreateAsync(impClaims);
        await audit.RecordAsync(project.OrgId, body.ProjectId, actorId, "admin.impersonate", "user", id.ToString());

        return Ok(new
        {
            token,
            expires_in_minutes = 15,
            user_id    = id,
            project_id = body.ProjectId,
            warning    = "This token grants access as the target user. Handle with extreme care."
        });
    }

    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("/admin/userlists")]
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

    [HttpGet("/admin/userlists/{id}")]
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

    [HttpGet("/admin/userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
if (!await db.UserLists.AnyAsync(ul => ul.Id == id)) return NotFound();
        var users = await db.Users
            .Where(u => u.UserListId == id)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] AdminCreateUserRequest body)
    {
var ul = await db.UserLists.FindAsync(id);
        if (ul == null) return NotFound();
        var username = body.Username ?? body.Email.Split('@')[0];
        string discriminator;
        do { discriminator = Random.Shared.Next(1000, 9999).ToString(); }
        while (await db.Users.AnyAsync(u => u.UserListId == id && u.Username == username && u.Discriminator == discriminator));
        var emailVerified = body.EmailVerified ?? false;
        var user = new User
        {
            UserListId = id, Username = username,
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash = passwords.Hash(body.Password),
            EmailVerified = emailVerified,
            EmailVerifiedAt = emailVerified ? DateTimeOffset.UtcNow : null,
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{user.Id}");
        if (ul.OrgId == null && ul.Immovable)
            await keto.WriteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");
        var assignedProjects = await db.Projects.Where(p => p.AssignedUserListId == id).ToListAsync();
        foreach (var project in assignedProjects)
            await roleService.AssignDefaultRoleAsync(project, user);
        return Created($"/admin/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email
        });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/userlists/{id}/users/{uid}")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/userlists")]
    public async Task<IActionResult> AdminCreateUserList([FromBody] AdminCreateUserListRequest body)
    {
var ul = new UserList { Name = body.Name, OrgId = body.OrgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/admin/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    // ── Service Accounts (system level) ───────────────────────────────────────

    [HttpGet("/admin/service-accounts")]
    public async Task<IActionResult> ListServiceAccounts()
    {
var sas = await db.ServiceAccounts
            .Where(sa => sa.IsSystem)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt }).ToListAsync();
        return Ok(sas);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/service-accounts")]
    public async Task<IActionResult> CreateSystemServiceAccount([FromBody] CreateSystemSaRequest body)
    {
var rootList = await GetOrCreateRootListAsync();
        var sa = new ServiceAccount
        {
            UserListId = rootList.Id, Name = body.Name, Description = body.Description,
            IsSystem = true, Active = true, CreatedBy = GetActorId(), CreatedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccounts.Add(sa);
        await db.SaveChangesAsync();
        await audit.RecordAsync(null, null, GetActorId(), "sa.created", "service_account", sa.Id.ToString());
        return Created($"/admin/service-accounts/{sa.Id}", new { sa.Id, sa.Name, sa.Description });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/service-accounts/{id}")]
    public async Task<IActionResult> GetSystemServiceAccount(Guid id)
    {
var sa = await db.ServiceAccounts
            .Include(sa => sa.PersonalAccessTokens)
            .Include(sa => sa.OrgRoles)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        return Ok(new
        {
            sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt,
            pats  = sa.PersonalAccessTokens.Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt }),
            roles = sa.OrgRoles.Select(r => new { r.Id, r.Role, r.GrantedAt })
        });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/service-accounts/{id}")]
    public async Task<IActionResult> DeleteSystemServiceAccount(Guid id)
    {
var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        db.ServiceAccounts.Remove(sa);
        await db.SaveChangesAsync();
        await audit.RecordAsync(null, null, GetActorId(), "sa.deleted", "service_account", id.ToString());
        return NoContent();
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/service-accounts/{id}/pat")]
    public async Task<IActionResult> ListSystemPats(Guid id)
    {
if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id && sa.IsSystem)) return NotFound();
        return Ok(await saService.ListPatsAsync(id));
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/service-accounts/{id}/pat")]
    public async Task<IActionResult> GenerateSystemPat(Guid id, [FromBody] GeneratePatRequest body)
    {
var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        var (raw, pat) = await patGen.GenerateAsync(id, body.Name, body.ExpiresAt, GetActorId());
        return Created($"/admin/service-accounts/{id}/pat/{pat.Id}", new { pat.Id, pat.Name, pat.ExpiresAt, token = raw });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/service-accounts/{id}/pat/{patId}")]
    public async Task<IActionResult> RevokeSystemPat(Guid id, Guid patId)
    {
if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id && sa.IsSystem)) return NotFound();
        try { await saService.RevokePat(patId, id); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/service-accounts/{id}/roles")]
    public async Task<IActionResult> ListSystemSaRoles(Guid id)
    {
var roles = await db.ServiceAccountOrgRoles
            .Where(r => r.ServiceAccountId == id)
            .Select(r => new { r.Id, r.Role, r.GrantedAt })
            .ToListAsync();
        return Ok(roles);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/service-accounts/{id}/roles")]
    public async Task<IActionResult> AssignSystemSaRole(Guid id, [FromBody] AssignSystemSaRoleRequest body)
    {
var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        var existing = await db.ServiceAccountOrgRoles.FirstOrDefaultAsync(r => r.ServiceAccountId == id && r.Role == body.Role);
        if (existing != null) return Ok(new { existing.Id, existing.Role, existing.GrantedAt });
        var role = new ServiceAccountOrgRole
        {
            ServiceAccountId = id, Role = body.Role,
            GrantedBy = GetActorId(), GrantedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccountOrgRoles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/admin/service-accounts/{id}/roles/{role.Id}", new { role.Id, role.Role, role.GrantedAt });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/service-accounts/{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveSystemSaRole(Guid id, Guid roleId)
    {
var role = await db.ServiceAccountOrgRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.ServiceAccountId == id);
        if (role == null) return NotFound();
        db.ServiceAccountOrgRoles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpGet("/admin/service-accounts/{id}/keys")]
    public async Task<IActionResult> GetSystemSaKeys(Guid id)
    {
var sa = await db.ServiceAccounts.FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        return Ok(await saService.GetKeysAsync(sa, hydra));
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/service-accounts/{id}/keys")]
    public async Task<IActionResult> AddSystemSaKey(Guid id, [FromBody] SaKeyRequest body)
    {
var sa = await db.ServiceAccounts.FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        try { var clientId = await saService.AddKeyAsync(sa, body.Jwk, hydra); return Ok(new { client_id = clientId }); }
        catch (Exception ex) { return BadRequest(new { error = "hydra_error", detail = ex.Message }); }
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/service-accounts/{id}/keys")]
    public async Task<IActionResult> RemoveSystemSaKey(Guid id)
    {
var sa = await db.ServiceAccounts.FirstOrDefaultAsync(sa => sa.Id == id && sa.IsSystem);
        if (sa == null) return NotFound();
        await saService.RemoveKeyAsync(sa, hydra);
        return Ok(new { message = "key_removed" });
    }

    // ── Org Admins ────────────────────────────────────────────────────────────

    [HttpGet("/admin/organisations/{id}/admins")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/organisations/{id}/admins")]
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
        return Created($"/admin/organisations/{id}/admins/{role.Id}", new { role.Id });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/organisations/{id}/admins/{roleId}")]
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

    // ── Org Service Accounts ──────────────────────────────────────────────────

    [HttpGet("/admin/organisations/{id}/service-accounts")]
    public async Task<IActionResult> ListOrgServiceAccounts(Guid id)
    {
var sas = await db.ServiceAccounts
            .Where(sa => sa.UserList.OrgId == id)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt })
            .ToListAsync();
        return Ok(sas);
    }

    // ── Projects (admin scope) ────────────────────────────────────────────────

    [HttpGet("/admin/projects")]
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

    [HttpGet("/admin/organisations/{id}/projects")]
    public async Task<IActionResult> AdminListOrgProjects(Guid id)
    {
var projects = await db.Projects
            .Where(p => p.OrgId == id).OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active }).ToListAsync();
        return Ok(projects);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/organisations/{id}/projects")]
    public async Task<IActionResult> AdminCreateProject(Guid id, [FromBody] AdminCreateProjectRequest body)
    {
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
                client_id    = $"client_{project.Id}",
                client_name  = $"Project: {project.Name}",
                redirect_uris = body.RedirectUris ?? [],
                grant_types  = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
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

    [HttpGet("/admin/projects/{id}")]
    public async Task<IActionResult> AdminGetProject(Guid id)
    {
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPatch("/admin/projects/{id}")]
    public async Task<IActionResult> AdminUpdateProject(Guid id, [FromBody] AdminUpdateProjectRequest body)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        if (body.Name != null) project.Name = body.Name;
        if (body.RequireRoleToLogin.HasValue)       project.RequireRoleToLogin       = body.RequireRoleToLogin.Value;
        if (body.AllowSelfRegistration.HasValue)    project.AllowSelfRegistration    = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue)   project.SmsVerificationEnabled   = body.SmsVerificationEnabled.Value;
        if (body.Active.HasValue)                   project.Active                   = body.Active.Value;
        if (body.AllowedEmailDomains != null)       project.AllowedEmailDomains      = body.AllowedEmailDomains;
        if (body.ClearDefaultRole == true)
            project.DefaultRoleId = null;
        else if (body.DefaultRoleId.HasValue)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == body.DefaultRoleId && r.ProjectId == id);
            if (role == null) return BadRequest(new { error = "invalid_default_role" });
            project.DefaultRoleId = body.DefaultRoleId;
        }
        if (body.LoginTheme != null) project.LoginTheme = body.LoginTheme;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/projects/{id}")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/projects/{id}/assign-userlist")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/projects/{id}/assign-userlist")]
    public async Task<IActionResult> AdminUnassignUserList(Guid id)
    {
var project = await db.Projects.FindAsync(id);
        if (project == null) return NotFound();
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, message = "userlist_unassigned" });
    }

    [HttpGet("/admin/projects/{id}/stats")]
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

    [HttpGet("/admin/projects/{id}/roles")]
    public async Task<IActionResult> AdminListRoles(Guid id)
    {
var roles = await db.Roles
            .Where(r => r.ProjectId == id)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank })
            .ToListAsync();
        return Ok(roles);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/projects/{id}/roles")]
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

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/projects/{id}/roles/{rid}")]
    public async Task<IActionResult> AdminDeleteRole(Guid id, Guid rid)
    {
var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == rid && r.ProjectId == id);
        if (role == null) return NotFound();
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Audit + Metrics ────────────────────────────────────────────────────────

    [HttpGet("/admin/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
var logs = await db.AuditLogs
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt, l.Metadata })
            .ToListAsync();
        return Ok(logs);
    }

    [HttpGet("/admin/metrics")]
    public async Task<IActionResult> GetMetrics()
    {
return Ok(new
        {
            org_count    = await db.Organisations.CountAsync(),
            active_users = await db.Users.CountAsync(u => u.Active),
            project_count = await db.Projects.CountAsync()
        });
    }

    [HttpGet("/admin/hydra/clients")]
    public async Task<IActionResult> ListHydraClients()
    {
var clients = await hydra.ListOAuth2ClientsAsync();
        return Ok(clients);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpPost("/admin/hydra/clients")]
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

    [HttpGet("/admin/hydra/clients/{id}")]
    public async Task<IActionResult> GetHydraClient(string id)
    {
var client = await hydra.GetOAuth2ClientAsync(id);
        if (client == null) return NotFound();
        return Ok(client);
    }

    [RequireManagementLevel(ManagementLevel.SuperAdmin)]
    [HttpDelete("/admin/hydra/clients/{id}")]
    public async Task<IActionResult> DeleteHydraClient(string id)
    {
await hydra.DeleteOAuth2ClientAsync(id);
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<UserList> GetOrCreateRootListAsync()
    {
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.OrgId == null && ul.Immovable);
        if (list != null) return list;
        list = new UserList { Name = "__system__", OrgId = null, Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }
}

// ── Request records ───────────────────────────────────────────────────────────
public record ImpersonateRequest(Guid ProjectId);
public record CreateOrgRequest(string Name, string Slug);
public record UpdateOrgRequest(string? Name);
public record AdminCreateUserRequest(string Email, string Password, string? Username, bool? EmailVerified);
public record AssignOrgAdminRequest(Guid UserId, string Role, Guid? ScopeId);
public record AdminCreateUserListRequest(string Name, Guid OrgId);
public record AdminCreateProjectRequest(string Name, string Slug, bool RequireRoleToLogin, string[]? RedirectUris);
public record AdminUpdateProjectRequest(string? Name, bool? RequireRoleToLogin, bool? AllowSelfRegistration, bool? EmailVerificationEnabled, bool? SmsVerificationEnabled,
    bool? Active, Guid? DefaultRoleId, bool? ClearDefaultRole, string[]? AllowedEmailDomains, Dictionary<string, object>? LoginTheme);
public record AdminAssignUserListRequest(Guid UserListId);
public record CreateSystemSaRequest(string Name, string? Description);
public record AssignSystemSaRoleRequest(string Role);
public record AdminUpdateUserRequest(string? Email, string? Username, string? DisplayName, string? Phone, bool? Active, bool? EmailVerified, bool? ClearLock, string? NewPassword);
public record AdminCreateRoleRequest(string Name, string? Description, int? Rank);
public record GeneratePatRequest(string Name, DateTimeOffset? ExpiresAt);
public record SaKeyRequest(System.Text.Json.JsonElement Jwk);
public record CreateHydraClientRequest(string ClientName, string[] GrantTypes, string[] RedirectUris, string? Scope);
