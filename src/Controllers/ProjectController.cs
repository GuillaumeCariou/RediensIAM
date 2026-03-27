using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
[RequireManagementLevel(ManagementLevel.ProjectManager)]
public class ProjectController(
    RediensIamDbContext db,
    RoleAssignmentService roleService,
    PatGenerationService patGen,
    KetoService keto,
    HydraAdminService hydra,
    ServiceAccountService saService,
    PasswordService passwords) : ControllerBase
{
    private TokenClaims Claims    => HttpContext.GetClaims()!;
    private Guid ActorId          => Claims.ParsedUserId;
    private bool IsSuperAdmin     => Claims.Roles.Contains(Roles.SuperAdmin);
    private Guid CallerOrgId      => Guid.TryParse(Claims.OrgId, out var g) ? g : Guid.Empty;

    // Prefer ?project_id= query param (org/super admin context) over token claims (project manager context)
    private Guid ProjectId
    {
        get
        {
            var q = HttpContext.Request.Query["project_id"].FirstOrDefault();
            if (q != null && Guid.TryParse(q, out var g)) return g;
            return Guid.Parse(Claims.ProjectId);
        }
    }

    // H1: every project load goes through this — returns null (→ 404) if the project
    // belongs to a different org, preventing cross-tenant access.
    private async Task<Project?> GetProjectAsync()
    {
        var isSuperAdmin = IsSuperAdmin;
        return await db.Projects
            .FirstOrDefaultAsync(p => p.Id == ProjectId && (isSuperAdmin || p.OrgId == CallerOrgId));
    }

    // ── Project info ──────────────────────────────────────────────────────────

    [HttpGet("/project/info")]
    public async Task<IActionResult> GetInfo()
    {
        var project = await db.Projects
            .Include(p => p.AssignedUserList)
            .Include(p => p.DefaultRole)
            .FirstOrDefaultAsync(p => p.Id == ProjectId && (IsSuperAdmin || p.OrgId == CallerOrgId));
        if (project == null) return NotFound();
        return Ok(new
        {
            project.Id, project.Name, project.Slug, project.Active,
            project.HydraClientId, project.RequireRoleToLogin,
            project.AssignedUserListId,
            AssignedUserListName   = project.AssignedUserList?.Name,
            project.DefaultRoleId,
            DefaultRoleName              = project.DefaultRole?.Name,
            project.MinPasswordLength,
            project.PasswordRequireUppercase,
            project.PasswordRequireLowercase,
            project.PasswordRequireDigit,
            project.PasswordRequireSpecial,
        });
    }

    [HttpPatch("/project/info")]
    public async Task<IActionResult> UpdateInfo([FromBody] UpdateProjectInfoRequest body)
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        if (body.Name != null)                     project.Name                    = body.Name;
        if (body.Active.HasValue)                  project.Active                  = body.Active.Value;
        if (body.RequireRoleToLogin.HasValue)       project.RequireRoleToLogin      = body.RequireRoleToLogin.Value;
        if (body.AllowSelfRegistration.HasValue)    project.AllowSelfRegistration   = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue)   project.SmsVerificationEnabled  = body.SmsVerificationEnabled.Value;
        if (body.AllowedEmailDomains != null)       project.AllowedEmailDomains     = body.AllowedEmailDomains;
        if (body.MinPasswordLength.HasValue)          project.MinPasswordLength          = Math.Max(0, body.MinPasswordLength.Value);
        if (body.PasswordRequireUppercase.HasValue)   project.PasswordRequireUppercase   = body.PasswordRequireUppercase.Value;
        if (body.PasswordRequireLowercase.HasValue)   project.PasswordRequireLowercase   = body.PasswordRequireLowercase.Value;
        if (body.PasswordRequireDigit.HasValue)       project.PasswordRequireDigit       = body.PasswordRequireDigit.Value;
        if (body.PasswordRequireSpecial.HasValue)     project.PasswordRequireSpecial     = body.PasswordRequireSpecial.Value;
        if (body.ClearDefaultRole == true)
            project.DefaultRoleId = null;
        else if (body.DefaultRoleId.HasValue)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == body.DefaultRoleId && r.ProjectId == ProjectId);
            if (role == null) return BadRequest(new { error = "invalid_default_role" });
            project.DefaultRoleId = body.DefaultRoleId;
        }
        if (body.LoginTheme != null) project.LoginTheme = body.LoginTheme;
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("/project/users")]
    public async Task<IActionResult> ListUsers()
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        var users = await db.Users
            .Where(u => u.UserListId == project.AssignedUserListId)
            .Select(u => new
            {
                u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt,
                roles = db.UserProjectRoles
                    .Where(r => r.UserId == u.Id && r.ProjectId == ProjectId)
                    .Select(r => new { r.RoleId, r.Role.Name }).ToList()
            }).ToListAsync();
        return Ok(users);
    }

    [HttpGet("/project/users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        // H2: verify the user belongs to this project's user list
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.UserListId == project.AssignedUserListId);
        if (user == null) return NotFound();
        var roles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => r.UserId == id && r.ProjectId == ProjectId)
            .Select(r => new { r.RoleId, r.Role.Name, r.Role.Rank }).ToListAsync();
        return Ok(new { user.Id, user.Username, user.Discriminator, user.Email, user.DisplayName, user.Active, roles });
    }

    [HttpPost("/project/users/{id}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleRequest body)
    {
        // RoleAssignmentService re-validates authority via Keto; the org check here prevents
        // leaking project existence across tenants.
        if (await GetProjectAsync() == null) return NotFound();
        try
        {
            await roleService.AssignProjectRoleAsync(ActorId, id, ProjectId, body.RoleId);
            return Ok(new { message = "role_assigned" });
        }
        catch (Exceptions.ForbiddenException ex)  { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.BadRequestException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex)   { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("/project/users/{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId)
    {
        if (await GetProjectAsync() == null) return NotFound();
        try
        {
            await roleService.RemoveProjectRoleAsync(ActorId, id, ProjectId, roleId);
            return NoContent();
        }
        catch (Exceptions.ForbiddenException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex)  { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("/project/users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateProjectUserRequest body)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return BadRequest(new { error = "no_user_list" });

        // M1: enforce project-level password policy
        if (project.MinPasswordLength > 0 && body.Password.Length < project.MinPasswordLength)
            return BadRequest(new { error = "password_too_short",     min_length = project.MinPasswordLength });
        if (project.PasswordRequireUppercase && !body.Password.Any(char.IsUpper))
            return BadRequest(new { error = "password_requires_uppercase" });
        if (project.PasswordRequireLowercase && !body.Password.Any(char.IsLower))
            return BadRequest(new { error = "password_requires_lowercase" });
        if (project.PasswordRequireDigit && !body.Password.Any(char.IsDigit))
            return BadRequest(new { error = "password_requires_digit" });
        if (project.PasswordRequireSpecial && !body.Password.Any(c => !char.IsLetterOrDigit(c)))
            return BadRequest(new { error = "password_requires_special" });

        var listId = project.AssignedUserListId.Value;
        var username = body.Username ?? body.Email.Split('@')[0];
        string discriminator;
        do { discriminator = Random.Shared.Next(1000, 9999).ToString(); }
        while (await db.Users.AnyAsync(u => u.UserListId == listId && u.Username == username && u.Discriminator == discriminator));

        var user = new User
        {
            UserListId = listId, Username = username,
            Discriminator = discriminator, Email = body.Email.ToLowerInvariant(),
            PasswordHash = passwords.Hash(body.Password),
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await keto.WriteRelationTupleAsync(Roles.KetoUserListsNamespace, listId.ToString(), "member", $"user:{user.Id}");
        await roleService.AssignDefaultRoleAsync(project, user);
        return Created($"/project/users/{user.Id}", new { user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email });
    }

    [HttpDelete("/project/users/{id}/sessions")]
    public async Task<IActionResult> ForceLogoutUser(Guid id)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        // Verify the target user belongs to this project before revoking (L2 fix)
        if (!await db.Users.AnyAsync(u => u.Id == id && u.UserListId == project.AssignedUserListId))
            return NotFound();
        await hydra.RevokeAllConsentSessionsAsync($"{project.OrgId}:{id}");
        return Ok(new { message = "sessions_revoked" });
    }

    [HttpGet("/project/stats")]
    public async Task<IActionResult> GetStats()
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();

        var totalUsers  = await db.Users.CountAsync(u => u.UserListId == project.AssignedUserListId);
        var activeUsers = await db.Users.CountAsync(u => u.UserListId == project.AssignedUserListId && u.Active);
        var usersByRole = await db.UserProjectRoles
            .Include(r => r.Role)
            .Where(r => r.ProjectId == ProjectId)
            .GroupBy(r => new { r.RoleId, r.Role.Name })
            .Select(g => new { role_id = g.Key.RoleId, role_name = g.Key.Name, count = g.Count() })
            .ToListAsync();

        return Ok(new { total_users = totalUsers, active_users = activeUsers, users_by_role = usersByRole });
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    [HttpGet("/project/roles")]
    public async Task<IActionResult> ListRoles()
    {
        if (await GetProjectAsync() == null) return NotFound();
        var roles = await db.Roles
            .Where(r => r.ProjectId == ProjectId)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank }).ToListAsync();
        return Ok(roles);
    }

    [HttpPost("/project/roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest body)
    {
        if (await GetProjectAsync() == null) return NotFound();
        var role = new Role
        {
            ProjectId = ProjectId, Name = body.Name,
            Description = body.Description, Rank = body.Rank,
            CreatedBy = ActorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/project/roles/{role.Id}", new { role.Id, role.Name, role.Rank });
    }

    [HttpPatch("/project/roles/{id}")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest body)
    {
        if (await GetProjectAsync() == null) return NotFound();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == ProjectId);
        if (role == null) return NotFound();
        if (body.Description != null) role.Description = body.Description;
        if (body.Rank.HasValue) role.Rank = body.Rank.Value;
        await db.SaveChangesAsync();
        return Ok(new { role.Id, role.Name, role.Rank });
    }

    [HttpDelete("/project/roles/{id}")]
    public async Task<IActionResult> DeleteRole(Guid id)
    {
        if (await GetProjectAsync() == null) return NotFound();
        var role = await db.Roles
            .Include(r => r.UserProjectRoles)
            .FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == ProjectId);
        if (role == null) return NotFound();
        foreach (var assignment in role.UserProjectRoles)
            await keto.DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, ProjectId.ToString(), $"role:{role.Name}", $"user:{assignment.UserId}");
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Service Accounts ──────────────────────────────────────────────────────

    [HttpGet("/project/service-accounts")]
    public async Task<IActionResult> ListServiceAccounts()
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        var sas = await db.ServiceAccounts
            .Where(sa => sa.UserListId == project.AssignedUserListId)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt }).ToListAsync();
        return Ok(sas);
    }

    [HttpPost("/project/service-accounts")]
    public async Task<IActionResult> CreateServiceAccount([FromBody] CreateServiceAccountRequest body)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return BadRequest(new { error = "project_not_ready" });
        var sa = new ServiceAccount
        {
            UserListId = project.AssignedUserListId.Value, Name = body.Name,
            Description = body.Description, Active = true, CreatedBy = ActorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccounts.Add(sa);
        await db.SaveChangesAsync();
        return Created($"/project/service-accounts/{sa.Id}", new { sa.Id, sa.Name });
    }

    [HttpGet("/project/service-accounts/{id}/pat")]
    public async Task<IActionResult> ListPats(Guid id)
    {
        // H3: verify the SA belongs to this project's assigned user list
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id && sa.UserListId == project.AssignedUserListId))
            return NotFound();
        return Ok(await saService.ListPatsAsync(id));
    }

    [HttpPost("/project/service-accounts/{id}/pat")]
    public async Task<IActionResult> GeneratePat(Guid id, [FromBody] GeneratePatRequest body)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        var sa = await db.ServiceAccounts.FirstOrDefaultAsync(
            sa => sa.Id == id && sa.UserListId == project.AssignedUserListId);
        if (sa == null) return NotFound();
        var (raw, pat) = await patGen.GenerateAsync(id, body.Name, body.ExpiresAt, ActorId);
        return Ok(new { pat.Id, pat.Name, token = raw, pat.ExpiresAt, message = "store_this_token_shown_once" });
    }

    [HttpDelete("/project/service-accounts/{id}/pat/{patId}")]
    public async Task<IActionResult> RevokePat(Guid id, Guid patId)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id && sa.UserListId == project.AssignedUserListId))
            return NotFound();
        try { await saService.RevokePat(patId, id); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Audit log + cleanup ───────────────────────────────────────────────────

    [HttpGet("/project/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        if (await GetProjectAsync() == null) return NotFound();
        var logs = await db.AuditLogs
            .Where(l => l.ProjectId == ProjectId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt, l.Metadata })
            .ToListAsync();
        return Ok(logs);
    }

    [HttpPost("/project/cleanup")]
    public async Task<IActionResult> Cleanup([FromBody] CleanupRequest body)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return BadRequest();
        var activeUserIds = await db.Users
            .Where(u => u.UserListId == project.AssignedUserListId)
            .Select(u => u.Id).ToHashSetAsync();
        var orphaned = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => r.ProjectId == ProjectId && !activeUserIds.Contains(r.UserId))
            .ToListAsync();
        if (!body.DryRun)
        {
            db.UserProjectRoles.RemoveRange(orphaned);
            foreach (var r in orphaned)
                await keto.DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, ProjectId.ToString(), $"role:{r.Role.Name}", $"user:{r.UserId}");
            await db.SaveChangesAsync();
        }
        return Ok(new { orphaned_roles_removed = orphaned.Count, dry_run = body.DryRun });
    }
}

public record UpdateProjectInfoRequest(string? Name, bool? Active, bool? RequireRoleToLogin, bool? AllowSelfRegistration,
    bool? EmailVerificationEnabled, bool? SmsVerificationEnabled, string[]? AllowedEmailDomains,
    Guid? DefaultRoleId, bool? ClearDefaultRole, Dictionary<string, object>? LoginTheme,
    int? MinPasswordLength, bool? PasswordRequireUppercase, bool? PasswordRequireLowercase,
    bool? PasswordRequireDigit, bool? PasswordRequireSpecial);
public record CreateProjectUserRequest(string Email, string? Username, string Password);
public record AssignRoleRequest(Guid RoleId);
public record CreateRoleRequest(string Name, string? Description, int Rank = 100);
public record UpdateRoleRequest(string? Description, int? Rank);
public record CreateServiceAccountRequest(string Name, string? Description);
public record CleanupRequest(bool DryRun = true, bool RemoveOrphanedRoles = true);
