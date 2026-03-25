using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Exceptions;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class OrgController(
    RediensIamDbContext db,
    HydraAdminService hydra,
    KetoService keto,
    PasswordService passwords,
    AuditLogService audit,
    RoleAssignmentService roleService,
    PatGenerationService patGen,
    ServiceAccountService saService,
    ILogger<OrgController> logger) : ControllerBase
{
    private TokenClaims Claims => HttpContext.GetClaims() ?? throw new UnauthorizedException("Not authenticated");
    private Guid OrgId   => Guid.TryParse(Claims.OrgId, out var g) ? g : Guid.Empty;
    private Guid ActorId => Claims.ParsedUserId;

    // ── Projects ──────────────────────────────────────────────────────────────

    [HttpGet("/org/projects")]
    public async Task<IActionResult> ListProjects([FromQuery] Guid? org_id)
    {
        Guid orgId;
        if (Guid.TryParse(Claims.OrgId, out var claimsOrgId))
            orgId = claimsOrgId;
        else if (org_id.HasValue && Claims.Roles.Contains(Roles.SuperAdmin))
            orgId = org_id.Value;
        else
            throw new ForbiddenException("No org context");
        var projects = await db.Projects
            .Where(p => p.OrgId == orgId)
            .Select(p => new { p.Id, p.Name, p.Slug, p.Active, p.AssignedUserListId, p.RequireRoleToLogin })
            .ToListAsync();
        return Ok(projects);
    }

    [HttpPost("/org/projects")]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequest body)
    {
        var orgId = OrgId;
        var project = new Project
        {
            OrgId = orgId, Name = body.Name, Slug = body.Slug,
            RequireRoleToLogin = body.RequireRoleToLogin,
            Active = true, CreatedBy = ActorId,
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
                metadata = new { project_id = project.Id.ToString(), org_id = orgId.ToString() }
            });
            project.HydraClientId = $"client_{project.Id}";
        }
        catch (Exception ex) { logger.LogWarning(ex, "Hydra client creation failed for project {ProjectId}", project.Id); }

        await keto.WriteRelationTupleAsync(Roles.KetoProjectsNamespace, project.Id.ToString(), "org", $"{Roles.KetoOrgsNamespace}:{orgId}");
        await db.SaveChangesAsync();
        await audit.RecordAsync(orgId, project.Id, ActorId, "project.created", "project", project.Id.ToString());
        return Created($"/org/projects/{project.Id}", new { project.Id, project.Name, project.Slug });
    }

    [HttpGet("/org/projects/{id}")]
    public async Task<IActionResult> GetProject(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        return Ok(project);
    }

    [HttpPatch("/org/projects/{id}")]
    public async Task<IActionResult> UpdateProject(Guid id, [FromBody] UpdateProjectRequest body)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        if (body.Name != null) project.Name = body.Name;
        if (body.RequireRoleToLogin.HasValue) project.RequireRoleToLogin = body.RequireRoleToLogin.Value;
        if (body.LoginTheme != null) project.LoginTheme = body.LoginTheme;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    [HttpDelete("/org/projects/{id}")]
    public async Task<IActionResult> DeleteProject(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        if (project.HydraClientId != null)
        {
            try { await hydra.DeleteOAuth2ClientAsync(project.HydraClientId); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete Hydra client {ClientId}", project.HydraClientId); }
        }
        await keto.DeleteAllProjectTuplesAsync(id.ToString());
        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, id, ActorId, "project.deleted", "project", id.ToString());
        return NoContent();
    }

    [HttpPost("/org/projects/{id}/assign-userlist")]
    public async Task<IActionResult> AssignUserList(Guid id, [FromBody] AssignUserListRequest body)
    {
        var orgId = OrgId;
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == orgId);
        if (project == null) return NotFound();
        var list = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == body.UserListId && ul.OrgId == orgId);
        if (list == null) return BadRequest(new { error = "userlist_not_in_org" });
        project.AssignedUserListId = body.UserListId;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.AssignedUserListId });
    }

    [HttpDelete("/org/projects/{id}/assign-userlist")]
    public async Task<IActionResult> UnassignUserList(Guid id)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == OrgId);
        if (project == null) return NotFound();
        project.AssignedUserListId = null;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, message = "userlist_unassigned" });
    }

    // ── UserLists ─────────────────────────────────────────────────────────────

    [HttpGet("/org/userlists")]
    public async Task<IActionResult> ListUserLists()
    {
        var lists = await db.UserLists
            .Where(ul => ul.OrgId == OrgId && !ul.Immovable)
            .Select(ul => new { ul.Id, ul.Name, ul.OrgId, ul.Immovable, ul.CreatedAt })
            .ToListAsync();
        return Ok(lists);
    }

    [HttpPost("/org/userlists")]
    public async Task<IActionResult> CreateUserList([FromBody] CreateUserListRequest body)
    {
        var ul = new UserList { Name = body.Name, OrgId = OrgId, Immovable = false, CreatedAt = DateTimeOffset.UtcNow };
        db.UserLists.Add(ul);
        await db.SaveChangesAsync();
        return Created($"/org/userlists/{ul.Id}", new { ul.Id, ul.Name });
    }

    [HttpGet("/org/userlists/{id}")]
    public async Task<IActionResult> GetUserList(Guid id)
    {
        var ul = await db.UserLists.Include(ul => ul.Users)
            .FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();
        var assignedProjects = await db.Projects.Where(p => p.AssignedUserListId == id)
            .Select(p => new { p.Id, p.Name }).ToListAsync();
        return Ok(new { ul.Id, ul.Name, ul.Immovable, user_count = ul.Users.Count, assigned_projects = assignedProjects });
    }

    [HttpDelete("/org/userlists/{id}")]
    public async Task<IActionResult> DeleteUserList(Guid id)
    {
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
        if (ul == null) return NotFound();
        if (ul.Immovable) return BadRequest(new { error = "cannot_delete_immovable" });
        if (await db.Projects.AnyAsync(p => p.AssignedUserListId == id))
            return BadRequest(new { error = "userlist_is_assigned_to_project" });
        db.UserLists.Remove(ul);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("/org/userlists/{id}/cleanup")]
    public async Task<IActionResult> CleanupUserList(Guid id, [FromBody] OrgCleanupRequest body)
    {
        var orgId = OrgId;
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == orgId)) return NotFound();

        var projectIds = await db.Projects.Where(p => p.AssignedUserListId == id).Select(p => p.Id).ToListAsync();
        var allUserIds = await db.Users.Where(u => u.UserListId == id).Select(u => u.Id).ToHashSetAsync();
        var orphanedRoles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => projectIds.Contains(r.ProjectId) && !allUserIds.Contains(r.UserId)).ToListAsync();

        var inactiveUsers = new List<Entities.User>();
        if (body.RemoveInactiveUsers)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-body.InactiveThresholdDays);
            inactiveUsers = await db.Users
                .Where(u => u.UserListId == id && (u.LastLoginAt == null || u.LastLoginAt < cutoff))
                .ToListAsync();
        }

        if (!body.DryRun)
        {
            if (body.RemoveOrphanedRoles)
            {
                db.UserProjectRoles.RemoveRange(orphanedRoles);
                foreach (var r in orphanedRoles)
                    await keto.DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, r.ProjectId.ToString(), $"role:{r.Role.Name}", $"user:{r.UserId}");
            }
            if (body.RemoveInactiveUsers)
            {
                foreach (var u in inactiveUsers)
                    await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{u.Id}");
                db.Users.RemoveRange(inactiveUsers);
            }
            await db.SaveChangesAsync();
        }

        return Ok(new
        {
            dry_run = body.DryRun,
            orphaned_roles_found = orphanedRoles.Count,
            orphaned_roles_removed = body.DryRun ? 0 : (body.RemoveOrphanedRoles ? orphanedRoles.Count : 0),
            inactive_users_found = inactiveUsers.Count,
            inactive_users_removed = body.DryRun ? 0 : (body.RemoveInactiveUsers ? inactiveUsers.Count : 0),
        });
    }

    [HttpGet("/org/userlists/{id}/users")]
    public async Task<IActionResult> ListUsersInList(Guid id)
    {
        if (!await db.UserLists.AnyAsync(ul => ul.Id == id && ul.OrgId == OrgId)) return NotFound();
        var users = await db.Users
            .Where(u => u.UserListId == id)
            .Select(u => new { u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost("/org/userlists/{id}/users")]
    public async Task<IActionResult> AddUserToList(Guid id, [FromBody] CreateUserRequest body)
    {
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == id && ul.OrgId == OrgId);
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
        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{user.Id}");
        return Created($"/org/userlists/{id}/users/{user.Id}", new
        {
            user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email
        });
    }

    [HttpPatch("/org/userlists/{id}/users/{uid}")]
    public async Task<IActionResult> UpdateUser(Guid id, Guid uid, [FromBody] UpdateUserRequest body)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        if (body.DisplayName != null) user.DisplayName = body.DisplayName;
        if (body.Active.HasValue) { user.Active = body.Active.Value; if (!body.Active.Value) user.DisabledAt = DateTimeOffset.UtcNow; }
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { user.Id, user.Active, user.DisplayName });
    }

    [HttpDelete("/org/userlists/{id}/users/{uid}")]
    public async Task<IActionResult> RemoveUser(Guid id, Guid uid)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == uid && u.UserListId == id && u.UserList.OrgId == OrgId);
        if (user == null) return NotFound();
        await keto.DeleteRelationTupleAsync(Roles.KetoUserListsNamespace, id.ToString(), "member", $"user:{uid}");
        db.Users.Remove(user);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "user.removed", "user", uid.ToString());
        return NoContent();
    }

    // ── Service Accounts ──────────────────────────────────────────────────────

    [HttpGet("/org/service-accounts")]
    public async Task<IActionResult> ListServiceAccounts()
    {
        var orgId = OrgId;
        var sas = await db.ServiceAccounts
            .Where(sa => sa.UserList.OrgId == orgId)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt })
            .ToListAsync();
        return Ok(sas);
    }

    [HttpPost("/org/service-accounts")]
    public async Task<IActionResult> CreateServiceAccount([FromBody] OrgCreateSaRequest body)
    {
        var ul = await db.UserLists.FirstOrDefaultAsync(ul => ul.Id == body.UserListId && ul.OrgId == OrgId);
        if (ul == null) return BadRequest(new { error = "userlist_not_in_org" });
        var sa = new ServiceAccount
        {
            UserListId = body.UserListId, Name = body.Name, Description = body.Description,
            Active = true, CreatedBy = ActorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccounts.Add(sa);
        await db.SaveChangesAsync();
        return Created($"/org/service-accounts/{sa.Id}", new { sa.Id, sa.Name });
    }

    [HttpGet("/org/service-accounts/{id}")]
    public async Task<IActionResult> GetServiceAccount(Guid id)
    {
        var orgId = OrgId;
        var sa = await db.ServiceAccounts
            .Include(sa => sa.PersonalAccessTokens)
            .Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == orgId);
        if (sa == null) return NotFound();
        return Ok(new
        {
            sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt, sa.HydraClientId,
            pats = sa.PersonalAccessTokens.Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt })
        });
    }

    [HttpDelete("/org/service-accounts/{id}")]
    public async Task<IActionResult> DeleteServiceAccount(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId);
        if (sa == null) return NotFound();
        db.ServiceAccounts.Remove(sa);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("/org/service-accounts/{id}/pat")]
    public async Task<IActionResult> ListPats(Guid id)
    {
        if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId)) return NotFound();
        return Ok(await saService.ListPatsAsync(id));
    }

    [HttpPost("/org/service-accounts/{id}/pat")]
    public async Task<IActionResult> GeneratePat(Guid id, [FromBody] OrgGeneratePatRequest body)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId);
        if (sa == null) return NotFound();
        var (raw, pat) = await patGen.GenerateAsync(id, body.Name, body.ExpiresAt, ActorId);
        return Ok(new { pat.Id, pat.Name, token = raw, pat.ExpiresAt, message = "store_this_token_shown_once" });
    }

    [HttpDelete("/org/service-accounts/{id}/pat/{patId}")]
    public async Task<IActionResult> RevokePat(Guid id, Guid patId)
    {
        if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId)) return NotFound();
        try { await saService.RevokePat(patId, id); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet("/org/service-accounts/{id}/keys")]
    public async Task<IActionResult> GetServiceAccountKeys(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId);
        if (sa == null) return NotFound();
        return Ok(await saService.GetKeysAsync(sa, hydra));
    }

    [HttpPost("/org/service-accounts/{id}/keys")]
    public async Task<IActionResult> AddServiceAccountKey(Guid id, [FromBody] SaKeyRequest body)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId);
        if (sa == null) return NotFound();
        try { var clientId = await saService.AddKeyAsync(sa, body.Jwk, hydra); return Ok(new { client_id = clientId }); }
        catch (Exception ex) { return BadRequest(new { error = "hydra_error", detail = ex.Message }); }
    }

    [HttpDelete("/org/service-accounts/{id}/keys")]
    public async Task<IActionResult> RemoveServiceAccountKey(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList)
            .FirstOrDefaultAsync(sa => sa.Id == id && sa.UserList.OrgId == OrgId);
        if (sa == null) return NotFound();
        await saService.RemoveKeyAsync(sa, hydra);
        return Ok(new { message = "key_removed" });
    }

    // ── Org-list managers ─────────────────────────────────────────────────────
    // These endpoints let an org admin manage who has management roles within their org.
    // Keto tuples are written/deleted via RoleAssignmentService to keep auth in sync.

    [HttpGet("/org/org-list/users")]
    public async Task<IActionResult> ListOrgListManagers()
    {
        var orgId = OrgId;
        var roles = await db.OrgRoles.Where(r => r.OrgId == orgId).Include(r => r.User).ToListAsync();
        var projectIds = roles.Where(r => r.ScopeId.HasValue).Select(r => r.ScopeId!.Value).Distinct().ToList();
        var projects = await db.Projects.Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        return Ok(roles.Select(r => new
        {
            r.Id, r.OrgId, r.UserId, r.Role, r.ScopeId, r.GrantedAt,
            user_name  = $"{r.User.Username}#{r.User.Discriminator}",
            user_email = r.User.Email,
            scope_name = r.ScopeId.HasValue && projects.TryGetValue(r.ScopeId.Value, out var p) ? p.Name : null
        }));
    }

    [HttpPost("/org/org-list/users")]
    public async Task<IActionResult> AssignOrgListManager([FromBody] OrgAssignManagerRequest body)
    {
        if (body.Role == Roles.SuperAdmin) return StatusCode(403, new { error = "cannot_grant_super_admin" });
        // Delegates to RoleAssignmentService which handles actor level check, DB write, and Keto tuple write
        await roleService.AssignManagementRoleAsync(ActorId, body.UserId, OrgId, body.Role, body.ScopeId);
        return Ok(new { message = "role_assigned" });
    }

    [HttpPatch("/org/org-list/users/{id}")]
    public async Task<IActionResult> UpdateOrgListManager(Guid id, [FromBody] OrgUpdateManagerRequest body)
    {
        var orgId = OrgId;
        var role = await db.OrgRoles.FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId);
        if (role == null) return NotFound();
        if (role.UserId == ActorId) return StatusCode(403, new { error = "cannot_modify_own_role" });

        if (body.Role != null && body.Role == Roles.SuperAdmin)
            return StatusCode(403, new { error = "cannot_grant_super_admin" });

        // Delete old Keto tuple before updating
        var oldSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await keto.DeleteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, oldSubject);

        if (body.Role != null) role.Role = body.Role;
        if (body.ScopeId != null) role.ScopeId = body.ScopeId;
        await db.SaveChangesAsync();

        // Write new Keto tuple
        var newSubject = role.ScopeId.HasValue ? $"user:{role.UserId}|project:{role.ScopeId}" : $"user:{role.UserId}";
        await keto.WriteRelationTupleAsync(Roles.KetoOrgsNamespace, orgId.ToString(), role.Role, newSubject);

        return Ok(new { role.Id, role.Role, role.ScopeId });
    }

    [HttpDelete("/org/org-list/users/{id}")]
    public async Task<IActionResult> RemoveOrgListManager(Guid id)
    {
        // Delegates to RoleAssignmentService which handles actor level check, DB delete, and Keto tuple delete
        await roleService.RemoveManagementRoleAsync(ActorId, id, OrgId);
        return NoContent();
    }

    [HttpGet("/org/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var orgId = OrgId;
        var logs = await db.AuditLogs
            .Where(l => l.OrgId == orgId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt })
            .ToListAsync();
        return Ok(logs);
    }
}

public record CreateProjectRequest(string Name, string Slug, bool RequireRoleToLogin, string[]? RedirectUris);
public record UpdateProjectRequest(string? Name, bool? RequireRoleToLogin, Dictionary<string, object>? LoginTheme);
public record AssignUserListRequest(Guid UserListId);
public record CreateUserListRequest(string Name);
public record CreateUserRequest(string Email, string Password, string? Username);
public record UpdateUserRequest(string? DisplayName, bool? Active);
public record OrgCreateSaRequest(string Name, string? Description, Guid UserListId);
public record OrgCleanupRequest(bool RemoveOrphanedRoles = true, bool RemoveInactiveUsers = false, int InactiveThresholdDays = 90, bool DryRun = true);
public record OrgGeneratePatRequest(string Name, DateTimeOffset? ExpiresAt);
public record OrgAssignManagerRequest(Guid UserId, string Role, Guid? ScopeId);
public record OrgUpdateManagerRequest(string? Role, Guid? ScopeId);
