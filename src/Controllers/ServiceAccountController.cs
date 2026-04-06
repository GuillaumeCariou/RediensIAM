using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// Unified service account management.
/// Access is determined per-action based on the caller's management level:
///   SuperAdmin   → all service accounts
///   OrgAdmin     → service accounts whose UserList belongs to their org
///   ProjectAdmin → service accounts in their project's assigned user list
/// </summary>
[ApiController]
[Route("service-accounts")]
public class ServiceAccountController(
    RediensIamDbContext db,
    PatService patService,
    AuditLogService audit) : ControllerBase
{
    private const string AuditSa = "service_account";

    private TokenClaims Claims     => HttpContext.GetClaims()!;
    private ManagementLevel Level  => Claims.GetManagementLevel();
    private Guid ActorId           => Claims.ParsedUserId;
    private Guid? CallerOrgId      => Guid.TryParse(Claims.OrgId, out var g) ? g : null;

    // Returns true if the caller has management access to the given SA.
    private async Task<bool> CanAccessAsync(ServiceAccount sa)
    {
        return Level switch
        {
            ManagementLevel.SuperAdmin   => true,
            ManagementLevel.OrgAdmin     => sa.UserList.OrgId == CallerOrgId,
            ManagementLevel.ProjectAdmin => await IsCallerProjectListAsync(sa.UserListId),
            _                            => false
        };
    }

    private async Task<bool> IsCallerProjectListAsync(Guid listId)
    {
        if (!Guid.TryParse(Claims.ProjectId, out var projectId)) return false;
        return await db.Projects.AnyAsync(p => p.Id == projectId && p.AssignedUserListId == listId
            && (Level == ManagementLevel.SuperAdmin || p.OrgId == CallerOrgId));
    }

    // ── List ──────────────────────────────────────────────────────────────────

    [HttpGet("")]
    public async Task<IActionResult> ListServiceAccounts()
    {
        if (Level == ManagementLevel.None) return Unauthorized();

        IQueryable<ServiceAccount> query = db.ServiceAccounts.Include(sa => sa.UserList);

        if (Level == ManagementLevel.OrgAdmin)
            query = query.Where(sa => sa.UserList.OrgId == CallerOrgId);
        else if (Level == ManagementLevel.ProjectAdmin)
        {
            if (!Guid.TryParse(Claims.ProjectId, out var projectId))
                return StatusCode(403, new { error = "no_project_context" });
            var listId = await db.Projects
                .Where(p => p.Id == projectId && p.OrgId == CallerOrgId)
                .Select(p => p.AssignedUserListId)
                .FirstOrDefaultAsync();
            if (listId == null) return NotFound();
            query = query.Where(sa => sa.UserListId == listId);
        }

        var sas = await query
            .Select(sa => new
            {
                sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt,
                sa.UserListId,
                org_id    = sa.UserList.OrgId,
                is_system = sa.UserList.OrgId == null && sa.UserList.Immovable
            })
            .ToListAsync();
        return Ok(sas);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [HttpPost("")]
    public async Task<IActionResult> CreateServiceAccount([FromBody] CreateSaRequest body)
    {
        if (Level == ManagementLevel.None) return Unauthorized();

        var list = await db.UserLists.FindAsync(body.UserListId);
        if (list == null) return BadRequest(new { error = "user_list_not_found" });

        // Validate caller has rights over the target list
        if (Level == ManagementLevel.OrgAdmin && list.OrgId != CallerOrgId)
            return StatusCode(403, new { error = "list_not_in_your_org" });

        if (Level == ManagementLevel.ProjectAdmin)
        {
            if (!Guid.TryParse(Claims.ProjectId, out var pId))
                return StatusCode(403, new { error = "no_project_context" });
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == pId && p.OrgId == CallerOrgId);
            if (project?.AssignedUserListId != body.UserListId)
                return StatusCode(403, new { error = "can_only_create_sa_in_your_project_list" });
        }

        // SuperAdmin may use any list, including the root list (system SA)

        var sa = new ServiceAccount
        {
            UserListId  = body.UserListId,
            Name        = body.Name,
            Description = body.Description,
            Active      = true,
            CreatedBy   = ActorId,
            CreatedAt   = DateTimeOffset.UtcNow
        };
        db.ServiceAccounts.Add(sa);
        await db.SaveChangesAsync();
        await audit.RecordAsync(list.OrgId, null, ActorId, "sa.created", AuditSa, sa.Id.ToString());
        return Created($"/service-accounts/{sa.Id}", new { sa.Id, sa.Name, sa.Description });
    }

    // ── Get / Delete ──────────────────────────────────────────────────────────

    [HttpGet("{id}")]
    public async Task<IActionResult> GetServiceAccount(Guid id)
    {
        var sa = await db.ServiceAccounts
            .Include(sa => sa.UserList)
            .Include(sa => sa.PersonalAccessTokens)
            .Include(sa => sa.Roles)
            .FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();

        return Ok(new
        {
            sa.Id, sa.Name, sa.Description, sa.Active, sa.LastUsedAt, sa.CreatedAt, sa.HydraClientId,
            sa.UserListId,
            org_id    = sa.UserList.OrgId,
            is_system = sa.UserList.OrgId == null && sa.UserList.Immovable,
            pats  = sa.PersonalAccessTokens.Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt }),
            roles = sa.Roles.Select(r => new { r.Id, r.Role, r.OrgId, r.ProjectId, r.GrantedAt })
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteServiceAccount(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        var orgId = sa.UserList.OrgId;
        db.ServiceAccounts.Remove(sa);
        await db.SaveChangesAsync();
        await audit.RecordAsync(orgId, null, ActorId, "sa.deleted", AuditSa, id.ToString());
        return NoContent();
    }

    // ── PAT management ────────────────────────────────────────────────────────

    [HttpGet("{id}/pat")]
    public async Task<IActionResult> ListPats(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        return Ok(await patService.ListPatsAsync(id));
    }

    [HttpPost("{id}/pat")]
    public async Task<IActionResult> GeneratePat(Guid id, [FromBody] GenerateSaPatRequest body)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        var (raw, pat) = await patService.GenerateAsync(id, body.Name, body.ExpiresAt, ActorId);
        return Ok(new { pat.Id, pat.Name, token = raw, pat.ExpiresAt, message = "store_this_token_shown_once" });
    }

    [HttpDelete("{id}/pat/{patId}")]
    public async Task<IActionResult> RevokePat(Guid id, Guid patId)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        try { await patService.RevokePat(patId, id); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    // ── API keys (Hydra JWK) ──────────────────────────────────────────────────

    [HttpGet("{id}/api-keys")]
    public async Task<IActionResult> GetApiKeys(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        return Ok(await patService.GetKeysAsync(sa));
    }

    [HttpPost("{id}/api-keys")]
    public async Task<IActionResult> AddApiKey(Guid id, [FromBody] SaApiKeyRequest body)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        try { var clientId = await patService.AddKeyAsync(sa, body.Jwk); return Ok(new { client_id = clientId }); }
        catch (Exception ex) { return BadRequest(new { error = "hydra_error", detail = ex.Message }); }
    }

    [HttpDelete("{id}/api-keys")]
    public async Task<IActionResult> RemoveApiKey(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        await patService.RemoveKeyAsync(sa);
        return Ok(new { message = "key_removed" });
    }

    // ── Role management ───────────────────────────────────────────────────────

    [HttpGet("{id}/roles")]
    public async Task<IActionResult> ListRoles(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        var roles = await db.ServiceAccountRoles
            .Where(r => r.ServiceAccountId == id)
            .Select(r => new { r.Id, r.Role, r.OrgId, r.ProjectId, r.GrantedAt })
            .ToListAsync();
        return Ok(roles);
    }

    [HttpPost("{id}/roles")]
    public async Task<IActionResult> AssignRole(Guid id, [FromBody] AssignSaRoleRequest body)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();

        var targetLevel = body.Role switch
        {
            Roles.SuperAdmin   => ManagementLevel.SuperAdmin,
            Roles.OrgAdmin     => ManagementLevel.OrgAdmin,
            Roles.ProjectAdmin => ManagementLevel.ProjectAdmin,
            _                  => ManagementLevel.None
        };
        if (targetLevel == ManagementLevel.None) return BadRequest(new { error = "unknown_role" });

        // Cannot grant a role higher than your own
        if (targetLevel < Level) return StatusCode(403, new { error = "insufficient_level_to_grant_this_role" });

        // OrgAdmin can only assign roles within their own org
        if (Level == ManagementLevel.OrgAdmin && body.OrgId != CallerOrgId)
            return StatusCode(403, new { error = "org_mismatch" });

        // ProjectAdmin can only assign project_admin within their own project
        if (Level == ManagementLevel.ProjectAdmin)
        {
            if (body.Role != Roles.ProjectAdmin)
                return StatusCode(403, new { error = "project_admin_can_only_assign_project_admin" });
            if (!Guid.TryParse(Claims.ProjectId, out var pId) || body.ProjectId != pId)
                return StatusCode(403, new { error = "project_mismatch" });
        }

        // Validate required scope fields
        if (body.Role == Roles.OrgAdmin && body.OrgId == null)
            return BadRequest(new { error = "org_id_required_for_org_admin" });
        if (body.Role == Roles.ProjectAdmin && (body.OrgId == null || body.ProjectId == null))
            return BadRequest(new { error = "org_id_and_project_id_required_for_project_admin" });

        var existing = await db.ServiceAccountRoles.FirstOrDefaultAsync(r =>
            r.ServiceAccountId == id && r.Role == body.Role
            && r.OrgId == body.OrgId && r.ProjectId == body.ProjectId);
        if (existing != null)
            return Ok(new { existing.Id, existing.Role, existing.OrgId, existing.ProjectId, existing.GrantedAt });

        var role = new ServiceAccountRole
        {
            ServiceAccountId = id,
            Role      = body.Role,
            OrgId     = body.OrgId,
            ProjectId = body.ProjectId,
            GrantedBy = ActorId,
            GrantedAt = DateTimeOffset.UtcNow
        };
        db.ServiceAccountRoles.Add(role);
        await db.SaveChangesAsync();
        await audit.RecordAsync(body.OrgId, body.ProjectId, ActorId, "sa.role.assigned",
            AuditSa, id.ToString(), new() { ["role"] = body.Role });
        return Created($"/service-accounts/{id}/roles/{role.Id}",
            new { role.Id, role.Role, role.OrgId, role.ProjectId, role.GrantedAt });
    }

    [HttpDelete("{id}/roles/{roleId}")]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();

        var role = await db.ServiceAccountRoles.FirstOrDefaultAsync(r => r.Id == roleId && r.ServiceAccountId == id);
        if (role == null) return NotFound();

        // Cannot remove a role higher than your own privilege
        var targetLevel = role.Role switch
        {
            Roles.SuperAdmin   => ManagementLevel.SuperAdmin,
            Roles.OrgAdmin     => ManagementLevel.OrgAdmin,
            Roles.ProjectAdmin => ManagementLevel.ProjectAdmin,
            _                  => ManagementLevel.None
        };
        if (targetLevel < Level) return StatusCode(403, new { error = "insufficient_level_to_remove_this_role" });

        db.ServiceAccountRoles.Remove(role);
        await db.SaveChangesAsync();
        await audit.RecordAsync(role.OrgId, role.ProjectId, ActorId, "sa.role.removed",
            AuditSa, id.ToString(), new() { ["role"] = role.Role });
        return NoContent();
    }
}

public record CreateSaRequest(string Name, string? Description, Guid UserListId);
public record GenerateSaPatRequest(string Name, DateTimeOffset? ExpiresAt);
public record SaApiKeyRequest(System.Text.Json.JsonElement Jwk);
public record AssignSaRoleRequest(string Role, Guid? OrgId, Guid? ProjectId);
