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

    /// <summary>
    /// H1: every project load goes through this — returns null (→ 404) if the project belongs to a
    /// different org, preventing cross-tenant access. A handler that queries <c>db.Projects</c>
    /// directly bypasses the only tenant check on this controller.
    /// </summary>
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
        var uris = project.HydraClientId is { } clientId
            ? await hydra.GetClientRedirectUrisAsync(clientId)
            : ([], []);
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
            // Everything below is accepted by the PATCH on this same route, and the console's
            // Authentication screen round-trips all of it: it reads the project, edits one field
            // and writes the whole set back. While the read omitted these the page fell back to
            // hardcoded defaults that looked exactly like a real configuration, so pressing Save
            // replaced the tenant's branding, providers and security settings with them. A read
            // that returns less than its own write accepts is a data-loss bug.
            project.LoginTheme,
            project.AllowSelfRegistration,
            project.CheckBreachedPasswords,
            project.EmailVerificationEnabled,
            project.SmsVerificationEnabled,
            project.AllowedEmailDomains,
            project.EmailFromName,
            project.IpAllowlist,
            project.AllowedScopes,
            // The console's settings screen round-trips what it reads. A read that returns less
            // than its own write accepts is a data-loss bug — the note above says so about the
            // theme, and it holds for these too.
            RedirectUris           = uris.RedirectUris,
            PostLogoutRedirectUris = uris.PostLogoutUris,
        });
    }

    [HttpPatch("info")]
    public async Task<IActionResult> UpdateInfo([FromBody] ProjectUpdateRequest body)
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();
        if (await ProjectUpdate.ApplyAsync(db, hydra, audit, appConfig, ActorId, project, body) is { } err) return err;
        await db.SaveChangesAsync();
        await audit.RecordAsync(project.OrgId, project.Id, ActorId, "project.updated", "project", project.Id.ToString());
        return Ok(new { project.Id, project.Name });
    }

    /// <summary>
    /// Shared with the org and admin project-update paths, which took the allowlist unchecked. An
    /// entry that does not parse makes IpInRange answer false for every address, so a typo locks
    /// the tenant out of its own project instead of reporting itself.
    /// </summary>
    internal static bool IsValidCidr(string entry)
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

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public async Task<IActionResult> ListUsers()
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();

        // Third instance of the same shape as the two stats handlers: no user list assigned means
        // no users, which is a fact about a project that exists. The console fetches this beside
        // the role list in one Promise.all, so a 404 here took the roles down with it and left the
        // whole members panel empty on every freshly created project.
        if (project.AssignedUserListId == null) return Ok(Array.Empty<object>());

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

    /// <summary>
    /// H2: the user id is caller-supplied, so it is matched against this project's own user list
    /// rather than looked up globally — otherwise any project admin could read any user in the
    /// deployment by guessing an id.
    /// </summary>
    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        var project = await GetProjectAsync();
        if (project?.AssignedUserListId == null) return NotFound();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.UserListId == project.AssignedUserListId);
        if (user == null) return NotFound();
        var roles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => r.UserId == id && r.ProjectId == ProjectId)
            .Select(r => new { r.RoleId, r.Role.Name, r.Role.Rank }).ToListAsync();
        return Ok(new { user.Id, user.Username, user.Discriminator, user.Email, user.DisplayName, user.Active, roles });
    }

    /// <summary>
    /// KetoService re-validates the caller's authority, so the <see cref="GetProjectAsync"/> call
    /// here is not the authorisation check — it is what stops the response distinguishing "not
    /// allowed" from "no such project" across tenants.
    /// </summary>
    [HttpPost("users/{id}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleRequest body)
    {
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
        // admin-created user start below the floor every self-service path enforces. The breach
        // check is deliberately not run here: this route never made that outbound call.
        var policy = PasswordPolicyService.CheckComposition(project, body.Password);
        if (policy == PasswordPolicyResult.TooShort)
            return BadRequest(new
            {
                error = "password_too_short",
                min_length = PasswordPolicyService.EffectiveMinimumLength(project),
            });
        if (policy != PasswordPolicyResult.Ok)
            return BadRequest(new { error = PasswordPolicyService.ErrorCode(policy) });

        var listId = project.AssignedUserListId.Value;

        // Third copy of the check the /admin path has. The unique index on (UserListId, Email)
        // would otherwise surface as a 500 rather than a conflict the caller can act on.
        var normalizedEmail = body.Email.ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.UserListId == listId && u.Email == normalizedEmail))
            return Conflict(new { error = "email_already_exists" });

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
        // L2: the Hydra subject is built from the caller's project, so without this membership
        // check a project admin could revoke the sessions of any user id in the deployment.
        var user = await db.Users.Include(u => u.UserList)
            .FirstOrDefaultAsync(u => u.Id == id && u.UserListId == project.AssignedUserListId);
        if (user == null) return NotFound();
        return await UserHelpers.ForceLogoutAsync(hydra, audit, ActorId, user, project.Id);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var project = await GetProjectAsync();
        if (project == null) return NotFound();

        // Same as the admin route: no user list means no users, not a missing project.
        if (project.AssignedUserListId == null)
            return Ok(new { total_users = 0, active_users = 0, users_by_role = Array.Empty<object>() });

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
        if (await GetProjectAsync() == null) return NotFound();
        var projectId = ProjectId;
        return await AuditLogQuery.PageAsync(db, l => l.ProjectId == projectId, limit, offset);
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

public record CreateProjectUserRequest(string Email, string? Username, string Password);
public record AssignRoleRequest([property: System.Text.Json.Serialization.JsonRequired] Guid RoleId);
public record CreateRoleRequest(string Name, string? Description, int? Rank);
public record UpdateRoleRequest(string? Description, int? Rank);
public record CleanupRequest(bool DryRun = true, bool RemoveOrphanedRoles = true);
