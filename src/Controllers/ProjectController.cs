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
[Route("project")]
[RequireManagementLevel(ManagementLevel.ProjectAdmin)]
public class ProjectController(
    RediensIamDbContext db,
    KetoService keto,
    HydraService hydra,
    PasswordService passwords,
    AppConfig appConfig) : ControllerBase
{
    private TokenClaims Claims    => HttpContext.GetClaims()!;
    private Guid ActorId          => Claims.ParsedUserId;
    private bool IsSuperAdmin     => Claims.Roles.Contains(Roles.SuperAdmin);
    private Guid CallerOrgId      => Guid.TryParse(Claims.OrgId, out var g) ? g : Guid.Empty;

    // OrgAdmin and SuperAdmin may target any project in their org via ?project_id=.
    // ProjectAdmin is locked to the project encoded in their own token claims.
    private Guid ProjectId
    {
        get
        {
            if (Claims.GetManagementLevel() <= ManagementLevel.OrgAdmin)
            {
                var q = HttpContext.Request.Query["project_id"].FirstOrDefault(); // NOSONAR: model binding not possible in a property getter
                if (q != null && Guid.TryParse(q, out var g)) return g;
            }
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

    [HttpGet("info")]
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
            project.HydraClientId, project.RequireRoleToLogin, project.RequireMfa,
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

    [HttpPatch("info")]
    public async Task<IActionResult> UpdateInfo([FromBody] UpdateProjectInfoRequest body)
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        if (body.Name != null)                     project.Name                    = body.Name;
        if (body.Active.HasValue)                  project.Active                  = body.Active.Value;
        if (body.RequireRoleToLogin.HasValue)       project.RequireRoleToLogin      = body.RequireRoleToLogin.Value;
        if (body.RequireMfa.HasValue)               project.RequireMfa              = body.RequireMfa.Value;
        if (body.AllowSelfRegistration.HasValue)    project.AllowSelfRegistration   = body.AllowSelfRegistration.Value;
        if (body.EmailVerificationEnabled.HasValue) project.EmailVerificationEnabled = body.EmailVerificationEnabled.Value;
        if (body.SmsVerificationEnabled.HasValue)   project.SmsVerificationEnabled  = body.SmsVerificationEnabled.Value;
        if (body.AllowedEmailDomains != null)       project.AllowedEmailDomains     = body.AllowedEmailDomains;
        if (body.MinPasswordLength.HasValue)          project.MinPasswordLength          = Math.Max(0, body.MinPasswordLength.Value);
        if (body.PasswordRequireUppercase.HasValue)   project.PasswordRequireUppercase   = body.PasswordRequireUppercase.Value;
        if (body.PasswordRequireLowercase.HasValue)   project.PasswordRequireLowercase   = body.PasswordRequireLowercase.Value;
        if (body.PasswordRequireDigit.HasValue)       project.PasswordRequireDigit       = body.PasswordRequireDigit.Value;
        if (body.PasswordRequireSpecial.HasValue)     project.PasswordRequireSpecial     = body.PasswordRequireSpecial.Value;
        var roleErr = await ApplyDefaultRoleAsync(project, body.ClearDefaultRole, body.DefaultRoleId);
        if (roleErr != null) return roleErr;
        var themeErr = ValidateLoginTheme(body.LoginTheme);
        if (themeErr != null) return themeErr;
        ApplyLoginTheme(project, body.LoginTheme);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return Ok(new { project.Id, project.Name });
    }

    private async Task<IActionResult?> ApplyDefaultRoleAsync(Project project, bool? clearRole, Guid? newRoleId)
    {
        if (clearRole == true)
        {
            project.DefaultRoleId = null;
        }
        else if (newRoleId.HasValue)
        {
            var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == newRoleId && r.ProjectId == ProjectId);
            if (role == null) return BadRequest(new { error = "invalid_default_role" });
            project.DefaultRoleId = newRoleId;
        }
        return null;
    }

    private IActionResult? ValidateLoginTheme(Dictionary<string, object>? theme)
    {
        if (theme == null) return null;
        if (theme.TryGetValue("logo_url", out var logoVal) && logoVal is string logoUrl && !string.IsNullOrEmpty(logoUrl))
        {
            if (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                return BadRequest(new { error = "logo_url_must_be_https" });
        }
        return null;
    }

    private void ApplyLoginTheme(Project project, Dictionary<string, object>? theme)
    {
        if (theme == null) return;
        project.LoginTheme = TotpEncryption.EncryptProviderSecretsInTheme(theme, project.LoginTheme, appConfig.ThemeEncKey)!;
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
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
                    .Select(r => new { Id = r.RoleId, r.Role.Name }).ToList()
            }).ToListAsync();
        return Ok(users);
    }

    [HttpGet("users/{id}")]
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

    [HttpPost("users/{id}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleRequest body)
    {
        // KetoService re-validates authority; the org check here prevents
        // leaking project existence across tenants.
        if (await GetProjectAsync() == null) return NotFound();
        try
        {
            await keto.AssignProjectRoleAsync(ActorId, id, ProjectId, body.RoleId);
            return Ok(new { message = "role_assigned" });
        }
        catch (Exceptions.ForbiddenException ex)  { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.BadRequestException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex)   { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("users/{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId)
    {
        if (await GetProjectAsync() == null) return NotFound();
        try
        {
            await keto.RemoveProjectRoleAsync(ActorId, id, ProjectId, roleId);
            return NoContent();
        }
        catch (Exceptions.ForbiddenException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex)  { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("users")]
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
        var discIter = 0;
        do
        {
            if (++discIter > 100) throw new InvalidOperationException("discriminator_space_exhausted");
            discriminator = Random.Shared.Next(1000, 9999).ToString();
        }
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
        await keto.AssignDefaultRoleAsync(project, user);
        return Created($"/project/users/{user.Id}", new { user.Id, username = $"{user.Username}#{user.Discriminator}", user.Email });
    }

    [HttpDelete("users/{id}/sessions")]
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

    [HttpGet("stats")]
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

    [HttpGet("roles")]
    public async Task<IActionResult> ListRoles()
    {
        if (await GetProjectAsync() == null) return NotFound();
        var roles = await db.Roles
            .Where(r => r.ProjectId == ProjectId)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank }).ToListAsync();
        return Ok(roles);
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest body)
    {
        if (await GetProjectAsync() == null) return NotFound();
        var role = new Role
        {
            ProjectId = ProjectId, Name = body.Name,
            Description = body.Description, Rank = body.Rank ?? 100,
            CreatedBy = ActorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/project/roles/{role.Id}", new { role.Id, role.Name, role.Rank });
    }

    [HttpPatch("roles/{id}")]
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

    [HttpDelete("roles/{id}")]
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

    // ── Audit log + cleanup ───────────────────────────────────────────────────

    [HttpGet("audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
        limit  = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        if (await GetProjectAsync() == null) return NotFound();
        var logs = await db.AuditLogs
            .Where(l => l.ProjectId == ProjectId)
            .OrderByDescending(l => l.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(l => new { l.Id, l.Action, l.OrgId, l.ProjectId, l.ActorId, l.TargetType, l.TargetId, l.IpAddress, l.CreatedAt, l.Metadata })
            .ToListAsync();
        return Ok(logs);
    }

    [HttpPost("cleanup")]
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

public record UpdateProjectInfoRequest(string? Name, bool? Active, bool? RequireRoleToLogin, bool? RequireMfa,
    bool? AllowSelfRegistration, bool? EmailVerificationEnabled, bool? SmsVerificationEnabled,
    string[]? AllowedEmailDomains, Guid? DefaultRoleId, bool? ClearDefaultRole,
    Dictionary<string, object>? LoginTheme, int? MinPasswordLength,
    bool? PasswordRequireUppercase, bool? PasswordRequireLowercase,
    bool? PasswordRequireDigit, bool? PasswordRequireSpecial);
public record CreateProjectUserRequest(string Email, string? Username, string Password);
public record AssignRoleRequest([property: System.Text.Json.Serialization.JsonRequired] Guid RoleId);
public record CreateRoleRequest(string Name, string? Description, int? Rank);
public record UpdateRoleRequest(string? Description, int? Rank);
public record CleanupRequest(bool DryRun = true, bool RemoveOrphanedRoles = true);
