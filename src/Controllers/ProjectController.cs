using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[ApiController]
public class ProjectController(
    RediensIamDbContext db,
    RoleAssignmentService roleService,
    PatGenerationService patGen,
    KetoService keto,
    HydraAdminService hydra) : ControllerBase
{
    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid OrgId => Guid.Parse(Claims.OrgId);
    private Guid ProjectId => Guid.Parse(Claims.ProjectId);
    private Guid ActorId => Guid.Parse(Claims.UserId.Contains(':') ? Claims.UserId.Split(':')[1] : Claims.UserId);

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("/project/users")]
    public async Task<IActionResult> ListUsers()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var projectId = Guid.Parse(claims.ProjectId);
        var project = await db.Projects.FindAsync(projectId);
        if (project?.AssignedUserListId == null) return NotFound();

        var users = await db.Users
            .Where(u => u.UserListId == project.AssignedUserListId)
            .Select(u => new
            {
                u.Id, u.Username, u.Discriminator, u.Email, u.DisplayName, u.Active, u.LastLoginAt,
                roles = db.UserProjectRoles
                    .Where(r => r.UserId == u.Id && r.ProjectId == projectId)
                    .Select(r => new { r.RoleId, r.Role.Name }).ToList()
            }).ToListAsync();
        return Ok(users);
    }

    [HttpGet("/project/users/{id}")]
    public async Task<IActionResult> GetUser(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var projectId = Guid.Parse(claims.ProjectId);
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();
        var roles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => r.UserId == id && r.ProjectId == projectId)
            .Select(r => new { r.RoleId, r.Role.Name, r.Role.Rank }).ToListAsync();
        return Ok(new { user.Id, user.Username, user.Discriminator, user.Email, user.DisplayName, user.Active, roles });
    }

    [HttpPost("/project/users/{id}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = claims.ParsedUserId;
        try
        {
            await roleService.AssignProjectRoleAsync(actorId, id, Guid.Parse(claims.ProjectId), body.RoleId);
            return Ok(new { message = "role_assigned" });
        }
        catch (Exceptions.ForbiddenException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.BadRequestException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("/project/users/{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = claims.ParsedUserId;
        try
        {
            await roleService.RemoveProjectRoleAsync(actorId, id, Guid.Parse(claims.ProjectId), roleId);
            return NoContent();
        }
        catch (Exceptions.ForbiddenException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // ── Force logout ──────────────────────────────────────────────────────────

    [HttpDelete("/project/users/{id}/sessions")]
    public async Task<IActionResult> ForceLogoutUser(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var projectId = Guid.Parse(claims.ProjectId);
        var project = await db.Projects.FindAsync(projectId);
        if (project == null) return NotFound();
        // Subject in Hydra is "{org_id}:{user_id}" for project users
        var subject = $"{project.OrgId}:{id}";
        await hydra.RevokeAllConsentSessionsAsync(subject);
        return Ok(new { message = "sessions_revoked" });
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    [HttpGet("/project/roles")]
    public async Task<IActionResult> ListRoles()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var roles = await db.Roles
            .Where(r => r.ProjectId == Guid.Parse(claims.ProjectId))
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank }).ToListAsync();
        return Ok(roles);
    }

    [HttpPost("/project/roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = claims.ParsedUserId;
        var role = new Role
        {
            ProjectId = Guid.Parse(claims.ProjectId), Name = body.Name,
            Description = body.Description, Rank = body.Rank,
            CreatedBy = actorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return Created($"/project/roles/{role.Id}", new { role.Id, role.Name, role.Rank });
    }

    [HttpPatch("/project/roles/{id}")]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateRoleRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == Guid.Parse(claims.ProjectId));
        if (role == null) return NotFound();
        if (body.Description != null) role.Description = body.Description;
        if (body.Rank.HasValue) role.Rank = body.Rank.Value;
        await db.SaveChangesAsync();
        return Ok(new { role.Id, role.Name, role.Rank });
    }

    [HttpDelete("/project/roles/{id}")]
    public async Task<IActionResult> DeleteRole(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == Guid.Parse(claims.ProjectId));
        if (role == null) return NotFound();
        db.Roles.Remove(role);
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ── Service Accounts ──────────────────────────────────────────────────────

    [HttpGet("/project/service-accounts")]
    public async Task<IActionResult> ListServiceAccounts()
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var project = await db.Projects.FindAsync(Guid.Parse(claims.ProjectId));
        if (project?.AssignedUserListId == null) return NotFound();
        var sas = await db.ServiceAccounts
            .Where(sa => sa.UserListId == project.AssignedUserListId)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt }).ToListAsync();
        return Ok(sas);
    }

    [HttpPost("/project/service-accounts")]
    public async Task<IActionResult> CreateServiceAccount([FromBody] CreateServiceAccountRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = claims.ParsedUserId;
        var project = await db.Projects.FindAsync(Guid.Parse(claims.ProjectId));
        if (project?.AssignedUserListId == null) return BadRequest(new { error = "project_not_ready" });
        var sa = new ServiceAccount
        {
            UserListId = project.AssignedUserListId.Value, Name = body.Name,
            Description = body.Description, Active = true, CreatedBy = actorId, CreatedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccounts.Add(sa);
        await db.SaveChangesAsync();
        return Created($"/project/service-accounts/{sa.Id}", new { sa.Id, sa.Name });
    }

    [HttpPost("/project/service-accounts/{id}/pat")]
    public async Task<IActionResult> GeneratePat(Guid id, [FromBody] GeneratePatRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var actorId = claims.ParsedUserId;
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        var (raw, pat) = await patGen.GenerateAsync(id, body.Name, body.ExpiresAt, actorId);
        return Ok(new { pat.Id, pat.Name, token = raw, pat.ExpiresAt, message = "store_this_token_shown_once" });
    }

    [HttpGet("/project/service-accounts/{id}/pat")]
    public async Task<IActionResult> ListPats(Guid id)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var projectId = Guid.Parse(claims.ProjectId);
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        var pats = await db.PersonalAccessTokens
            .Where(p => p.ServiceAccountId == id)
            .Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt }).ToListAsync();
        return Ok(pats);
    }

    [HttpDelete("/project/service-accounts/{id}/pat/{patId}")]
    public async Task<IActionResult> RevokePat(Guid id, Guid patId)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        var pat = await db.PersonalAccessTokens.FirstOrDefaultAsync(p => p.Id == patId && p.ServiceAccountId == id);
        if (pat == null) return NotFound();
        db.PersonalAccessTokens.Remove(pat);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("/project/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var logs = await db.AuditLogs
            .Where(l => l.ProjectId == Guid.Parse(claims.ProjectId))
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();
        return Ok(logs);
    }

    [HttpPost("/project/cleanup")]
    public async Task<IActionResult> Cleanup([FromBody] CleanupRequest body)
    {
        if (HttpContext.GetClaims() is not { } claims) return Unauthorized();
        var projectId = Guid.Parse(claims.ProjectId);
        var project = await db.Projects.FindAsync(projectId);
        if (project?.AssignedUserListId == null) return BadRequest();

        var activeUserIds = await db.Users
            .Where(u => u.UserListId == project.AssignedUserListId)
            .Select(u => u.Id).ToHashSetAsync();

        var orphaned = await db.UserProjectRoles
            .Include(r => r.Role)
            .Where(r => r.ProjectId == projectId && !activeUserIds.Contains(r.UserId))
            .ToListAsync();

        if (!body.DryRun)
        {
            db.UserProjectRoles.RemoveRange(orphaned);
            foreach (var r in orphaned)
                await keto.DeleteRelationTupleAsync("Projects", projectId.ToString(), $"role:{r.Role.Name}", $"user:{r.UserId}");
            await db.SaveChangesAsync();
        }
        return Ok(new { orphaned_roles_removed = orphaned.Count, dry_run = body.DryRun });
    }
}

public record AssignRoleRequest(Guid RoleId);
public record CreateRoleRequest(string Name, string? Description, int Rank = 100);
public record UpdateRoleRequest(string? Description, int? Rank);
public record CreateServiceAccountRequest(string Name, string? Description);
public record GeneratePatRequest(string Name, DateTimeOffset? ExpiresAt);
public record CleanupRequest(bool DryRun = true, bool RemoveOrphanedRoles = true);
