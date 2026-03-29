using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class SystemAdminController(
    RediensIamDbContext db,
    HydraService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    AppConfig appConfig,
    IEmailService emailService,
    ILogger<SystemAdminController> logger) : ControllerBase
{
    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid GetActorId() => Claims.ParsedUserId;

    // ── Organisations ─────────────────────────────────────────────────────────

    [HttpGet("/admin/organizations")]
    public async Task<IActionResult> ListOrgs()
    {
var orgs = await db.Organisations
            .Where(o => o.Slug != "__system__")
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt }).ToListAsync();
        return Ok(orgs);
    }

    [HttpGet("/admin/organizations/{id}")]
    public async Task<IActionResult> GetOrg(Guid id)
    {
        var org = await db.Organisations
            .Where(o => o.Id == id)
            .Select(o => new { o.Id, o.Name, o.Slug, o.Active, o.SuspendedAt, o.CreatedAt, o.UpdatedAt, o.OrgListId, o.CreatedBy })
            .FirstOrDefaultAsync();
        if (org == null) return NotFound();
        return Ok(org);
    }

    [HttpPost("/admin/organizations")]
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
        return Created($"/admin/organizations/{org.Id}", new { org.Id, org.Name, org.Slug, org_list_id = orgList.Id });
    }

    [HttpPatch("/admin/organizations/{id}")]
    public async Task<IActionResult> UpdateOrg(Guid id, [FromBody] UpdateOrgRequest body)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        if (body.Name != null) org.Name = body.Name;
        org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { org.Id, org.Name });
    }

    [HttpPost("/admin/organizations/{id}/suspend")]
    public async Task<IActionResult> SuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = false; org.SuspendedAt = DateTimeOffset.UtcNow; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.suspended", "organisation", id.ToString());
        return Ok(new { message = "org_suspended" });
    }

    [HttpPost("/admin/organizations/{id}/unsuspend")]
    public async Task<IActionResult> UnsuspendOrg(Guid id)
    {
var org = await db.Organisations.FindAsync(id);
        if (org == null) return NotFound();
        org.Active = true; org.SuspendedAt = null; org.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(id, null, GetActorId(), "org.unsuspended", "organisation", id.ToString());
        return Ok(new { message = "org_unsuspended" });
    }

    [HttpDelete("/admin/organizations/{id}")]
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

    [HttpDelete("/admin/users/{id}/sessions")]
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
            await keto.AssignDefaultRoleAsync(project, user);
        return Created($"/admin/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email
        });
    }

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

    [HttpPost("/admin/userlists")]
    public async Task<IActionResult> AdminCreateUserList([FromBody] AdminCreateUserListRequest body)
    {
var ul = new UserList { Name = body.Name, OrgId = body.OrgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/admin/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    // ── Org Admins ────────────────────────────────────────────────────────────

    [HttpGet("/admin/organizations/{id}/admins")]
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

    [HttpPost("/admin/organizations/{id}/admins")]
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

    [HttpDelete("/admin/organizations/{id}/admins/{roleId}")]
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


    [HttpPost("/admin/organizations/{id}/projects")]
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

    [HttpPut("/admin/projects/{id}/userlist")]
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

    [HttpDelete("/admin/projects/{id}/userlist")]
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

    [HttpDelete("/admin/projects/{id}/roles/{rid}")]
    public async Task<IActionResult> AdminDeleteRole(Guid id, Guid rid)
    {
var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == rid && r.ProjectId == id);
        if (role == null) return NotFound();
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Email overview ────────────────────────────────────────────────────────

    [HttpGet("/admin/email/overview")]
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

    [HttpGet("/admin/organizations/{id}/smtp")]
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

    [HttpPut("/admin/organizations/{id}/smtp")]
    public async Task<IActionResult> UpsertOrgSmtp(Guid id, [FromBody] AdminUpsertSmtpRequest body)
    {
        if (!await db.Organisations.AnyAsync(o => o.Id == id)) return NotFound();
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        var key    = Convert.FromHexString(appConfig.TotpSecretEncryptionKey);

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
                    ? TotpEncryption.Encrypt(key, Encoding.UTF8.GetBytes(body.Password))
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
                config.PasswordEnc = TotpEncryption.Encrypt(key, Encoding.UTF8.GetBytes(body.Password));
            config.FromAddress = body.FromAddress;
            config.FromName    = body.FromName;
            config.UpdatedAt   = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return Ok(new { message = "smtp_config_saved" });
    }

    [HttpDelete("/admin/organizations/{id}/smtp")]
    public async Task<IActionResult> DeleteOrgSmtp(Guid id)
    {
        var config = await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == id);
        if (config == null) return NoContent();
        db.OrgSmtpConfigs.Remove(config);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("/admin/organizations/{id}/smtp/test")]
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

    [HttpDelete("/admin/hydra/clients/{id}")]
    public async Task<IActionResult> DeleteHydraClient(string id)
    {
await hydra.DeleteOAuth2ClientAsync(id);
        return NoContent();
    }

}

// ── Request records ───────────────────────────────────────────────────────────
public record CreateOrgRequest(string Name, string Slug);
public record UpdateOrgRequest(string? Name);
public record AdminCreateUserRequest(string Email, string Password, string? Username, bool? EmailVerified);
public record AssignOrgAdminRequest(Guid UserId, string Role, Guid? ScopeId);
public record AdminCreateUserListRequest(string Name, Guid OrgId);
public record AdminCreateProjectRequest(string Name, string Slug, bool RequireRoleToLogin, string[]? RedirectUris);
public record AdminUpdateProjectRequest(string? Name, bool? RequireRoleToLogin, bool? AllowSelfRegistration, bool? EmailVerificationEnabled, bool? SmsVerificationEnabled,
    bool? Active, Guid? DefaultRoleId, bool? ClearDefaultRole, string[]? AllowedEmailDomains, Dictionary<string, object>? LoginTheme);
public record AdminAssignUserListRequest(Guid UserListId);
public record AdminUpdateUserRequest(string? Email, string? Username, string? DisplayName, string? Phone, bool? Active, bool? EmailVerified, bool? ClearLock, string? NewPassword);
public record AdminCreateRoleRequest(string Name, string? Description, int? Rank);
public record CreateHydraClientRequest(string ClientName, string[] GrantTypes, string[] RedirectUris, string? Scope);
public record AdminUpsertSmtpRequest(string Host, int Port, bool StartTls, string? Username, string? Password, string FromAddress, string FromName);
