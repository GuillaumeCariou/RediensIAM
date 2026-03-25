using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
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
    HydraAdminService hydra,
    ServiceAccountService saService) : ControllerBase
{
    private TokenClaims Claims    => HttpContext.GetClaims()!;
    private Guid OrgId            => Guid.Parse(Claims.OrgId);
    private Guid ProjectId        => Guid.Parse(Claims.ProjectId);
    private Guid ActorId          => Claims.ParsedUserId;

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("/project/users")]
    public async Task<IActionResult> ListUsers()
    {
        var project = await db.Projects.FindAsync(ProjectId);
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
        var user = await db.Users.FindAsync(id);
        if (user == null) return NotFound();
        var roles = await db.UserProjectRoles.Include(r => r.Role)
            .Where(r => r.UserId == id && r.ProjectId == ProjectId)
            .Select(r => new { r.RoleId, r.Role.Name, r.Role.Rank }).ToListAsync();
        return Ok(new { user.Id, user.Username, user.Discriminator, user.Email, user.DisplayName, user.Active, roles });
    }

    [HttpPost("/project/users/{id}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignRoleRequest body)
    {
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
        try
        {
            await roleService.RemoveProjectRoleAsync(ActorId, id, ProjectId, roleId);
            return NoContent();
        }
        catch (Exceptions.ForbiddenException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exceptions.NotFoundException ex)  { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("/project/users/{id}/sessions")]
    public async Task<IActionResult> ForceLogoutUser(Guid id)
    {
        var project = await db.Projects.FindAsync(ProjectId);
        if (project == null) return NotFound();
        await hydra.RevokeAllConsentSessionsAsync($"{project.OrgId}:{id}");
        return Ok(new { message = "sessions_revoked" });
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    [HttpGet("/project/roles")]
    public async Task<IActionResult> ListRoles()
    {
        var roles = await db.Roles
            .Where(r => r.ProjectId == ProjectId)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.Id, r.Name, r.Description, r.Rank }).ToListAsync();
        return Ok(roles);
    }

    [HttpPost("/project/roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest body)
    {
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
        var project = await db.Projects.FindAsync(ProjectId);
        if (project?.AssignedUserListId == null) return NotFound();
        var sas = await db.ServiceAccounts
            .Where(sa => sa.UserListId == project.AssignedUserListId)
            .Select(sa => new { sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt }).ToListAsync();
        return Ok(sas);
    }

    [HttpPost("/project/service-accounts")]
    public async Task<IActionResult> CreateServiceAccount([FromBody] CreateServiceAccountRequest body)
    {
        var project = await db.Projects.FindAsync(ProjectId);
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
        if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id)) return NotFound();
        return Ok(await saService.ListPatsAsync(id));
    }

    [HttpPost("/project/service-accounts/{id}/pat")]
    public async Task<IActionResult> GeneratePat(Guid id, [FromBody] GeneratePatRequest body)
    {
        var sa = await db.ServiceAccounts.FindAsync(id);
        if (sa == null) return NotFound();
        var (raw, pat) = await patGen.GenerateAsync(id, body.Name, body.ExpiresAt, ActorId);
        return Ok(new { pat.Id, pat.Name, token = raw, pat.ExpiresAt, message = "store_this_token_shown_once" });
    }

    [HttpDelete("/project/service-accounts/{id}/pat/{patId}")]
    public async Task<IActionResult> RevokePat(Guid id, Guid patId)
    {
        if (!await db.ServiceAccounts.AnyAsync(sa => sa.Id == id)) return NotFound();
        try { await saService.RevokePat(patId, id); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── Audit log + cleanup ───────────────────────────────────────────────────

    [HttpGet("/project/audit-log")]
    public async Task<IActionResult> GetAuditLog([FromQuery] int limit = 50, [FromQuery] int offset = 0)
    {
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
        var project = await db.Projects.FindAsync(ProjectId);
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

public record AssignRoleRequest(Guid RoleId);
public record CreateRoleRequest(string Name, string? Description, int Rank = 100);
public record UpdateRoleRequest(string? Description, int? Rank);
public record CreateServiceAccountRequest(string Name, string? Description);
public record CleanupRequest(bool DryRun = true, bool RemoveOrphanedRoles = true);
