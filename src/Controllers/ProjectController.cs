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
    AuditLogService audit,
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
#pragma warning disable S6932 // model binding not available in a property getter
            // The ?project_id= escalation branch: deciding it from a claim is the same defect
            // one tier down from R-22, so it reads the live-verified grant (S-1).
            if (HttpContext.GetGrantedLevel() is { } grant && grant.IsAtLeast(ManagementLevel.OrgAdmin))
            {
                var q = HttpContext.Request.Query["project_id"].FirstOrDefault();
                if (q != null && Guid.TryParse(q, out var g)) return g;
            }
#pragma warning restore S6932
            // Guid.Parse threw a FormatException — surfacing as a 500 — when a super admin
            // called /project/* without ?project_id=. Empty means "no project context", which
            // GetProjectAsync turns into a clean 404.
            return Guid.TryParse(Claims.ProjectId, out var fromClaims) ? fromClaims : Guid.Empty;
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
        var allowlistErr = ApplyIpAllowlist(project, body.IpAllowlist);
        if (allowlistErr != null) return allowlistErr;
        if (await MfaDowngradeGuard.CheckAsync(db, audit, ActorId, project, body.RequireMfa, body.ConfirmMfaDowngrade) is { } mfaErr)
            return mfaErr;
        ApplyProjectFields(project, body);
        var roleErr = await ApplyDefaultRoleAsync(project, body.ClearDefaultRole, body.DefaultRoleId);
        if (roleErr != null) return roleErr;
        var themeErr = ValidateLoginTheme(body.LoginTheme);
        if (themeErr != null) return themeErr;
        ApplyLoginTheme(project, body.LoginTheme);
        project.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "project.updated", "project", project.Id.ToString());
        return Ok(new { project.Id, project.Name });
    }

    private static void ApplyProjectFields(Project project, UpdateProjectInfoRequest body)
    {
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
        if (body.CheckBreachedPasswords.HasValue)     project.CheckBreachedPasswords     = body.CheckBreachedPasswords.Value;
        if (body.ClearEmailFromName == true)          project.EmailFromName              = null;
        else if (body.EmailFromName != null)          project.EmailFromName              = body.EmailFromName;
    }

    /// <summary>
    /// Validates every entry before storing. An unparseable CIDR silently matches nothing in
    /// <c>IpInRange</c>, which locks the whole tenant out of its own project instead of
    /// reporting the typo.
    /// </summary>
    private BadRequestObjectResult? ApplyIpAllowlist(Project project, string[]? allowlist)
    {
        if (allowlist == null) return null;

        var invalid = allowlist.Where(entry => !IsValidCidr(entry)).ToArray();
        if (invalid.Length > 0)
            return BadRequest(new { error = "invalid_ip_allowlist", invalid });

        project.IpAllowlist = allowlist;
        return null;
    }

    private static bool IsValidCidr(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        var parts = entry.Split('/');
        if (parts.Length > 2) return false;
        if (!System.Net.IPAddress.TryParse(parts[0], out var address)) return false;
        if (parts.Length == 1) return true;

        if (!int.TryParse(parts[1], out var prefix)) return false;
        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return prefix >= 0 && prefix <= maxPrefix;
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

    private BadRequestObjectResult? ValidateLoginTheme(Dictionary<string, object>? theme) =>
        LoginThemeValidator.Validate(theme) is { } error ? BadRequest(new { error }) : null;

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
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        try
        {
            await keto.AssignProjectRoleAsync(ActorId, id, ProjectId, body.RoleId);
            await audit.RecordAsync(project.OrgId, project.Id, ActorId, "role.assigned", "user", id.ToString(),
                new() { ["role_id"] = body.RoleId.ToString() });
            return Ok(new { message = "role_assigned" });
        }
        catch (Exceptions.ForbiddenException ex)  { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.BadRequestException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex)   { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("users/{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId)
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        try
        {
            await keto.RemoveProjectRoleAsync(ActorId, id, ProjectId, roleId);
            await audit.RecordAsync(project.OrgId, project.Id, ActorId, "role.revoked", "user", id.ToString(),
                new() { ["role_id"] = roleId.ToString() });
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

        // M1: enforce project-level password policy. The minimum is the project's own setting or
        // the absolute floor, whichever is higher — reading MinPasswordLength directly let an
        // admin-created user start below the floor every self-service path enforces.
        var minLength = PasswordPolicyService.EffectiveMinimumLength(project);
        if (body.Password.Length < minLength)
            return BadRequest(new { error = "password_too_short",     min_length = minLength });
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
            discriminator = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000, 10000).ToString();
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
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "user.created", "user", user.Id.ToString());
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
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "session.revoked", "user", id.ToString());
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
        if (Roles.ProjectRoleNameError(body.Name) is { } nameErr)
            return BadRequest(new { error = nameErr, reserved = Roles.Management });
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        var role = new Role
        {
            ProjectId = ProjectId, Name = body.Name,
            Description = body.Description, Rank = body.Rank ?? 100,
            CreatedBy = ActorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "role.created", "role", role.Id.ToString(),
            new() { ["name"] = role.Name });
        return Created($"/project/roles/{role.Id}", new { role.Id, role.Name, role.Rank });
    }

    [HttpPatch("roles/{id}")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest body)
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == ProjectId);
        if (role == null) return NotFound();
        if (body.Description != null) role.Description = body.Description;
        if (body.Rank.HasValue) role.Rank = body.Rank.Value;
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "role.updated", "role", role.Id.ToString());
        return Ok(new { role.Id, role.Name, role.Rank });
    }

    [HttpDelete("roles/{id}")]
    public async Task<IActionResult> DeleteRole(Guid id)
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        var role = await db.Roles
            .Include(r => r.UserProjectRoles)
            .FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == ProjectId);
        if (role == null) return NotFound();
        foreach (var assignment in role.UserProjectRoles)
            await keto.DeleteRelationTupleAsync(Roles.KetoProjectsNamespace, ProjectId.ToString(), $"role:{role.Name}", $"user:{assignment.UserId}");
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "role.deleted", "role", id.ToString(),
            new() { ["name"] = role.Name });
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
    bool? PasswordRequireDigit, bool? PasswordRequireSpecial,
    // Sent by the admin console. Previously absent from this record, so System.Text.Json
    // dropped them and the API answered 200 while applying nothing — an operator could
    // enable an IP allowlist that never took effect.
    string[]? IpAllowlist, bool? CheckBreachedPasswords,
    string? EmailFromName, bool? ClearEmailFromName,
    // Acknowledges the 409 from MfaDowngradeGuard. Only read when require_mfa goes true → false.
    bool? ConfirmMfaDowngrade = null);
public record CreateProjectUserRequest(string Email, string? Username, string Password);
public record AssignRoleRequest([property: System.Text.Json.Serialization.JsonRequired] Guid RoleId);
public record CreateRoleRequest(string Name, string? Description, int? Rank);
public record UpdateRoleRequest(string? Description, int? Rank);
public record CleanupRequest(bool DryRun = true, bool RemoveOrphanedRoles = true);
