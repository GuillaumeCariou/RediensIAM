using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
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
// Every action gates on the management level carried by the token, which is a snapshot taken
// when that token was minted. This filter is what re-verifies the level against Keto, so a
// revoked administrator cannot keep minting credentials on the deployment's most privileged
// service accounts for the rest of the token's lifetime. ProjectAdmin is the least-privileged
// level any action here admits; the per-action checks below still apply on top of it.
[RequireManagementLevel(ManagementLevel.ProjectAdmin)]
public class ServiceAccountController(
    RediensIamDbContext db,
    PatService patService,
    AuditLogService audit,
    AppConfig appConfig) : ControllerBase
{
    private const string AuditSa = "service_account";

    private TokenClaims Claims     => HttpContext.GetClaims()!;
    private ManagementLevel Level  => Claims.GetManagementLevel();
    private Guid ActorId           => Claims.ParsedUserId;
    // Guid.Empty, never null. A null here compared equal to UserList.OrgId IS NULL — the
    // __system__ list — so a token whose org_id failed to parse gained access to the most
    // privileged service accounts in the deployment. Every other controller already uses
    // Guid.Empty, which matches no real row.
    private Guid CallerOrgId       => Guid.TryParse(Claims.OrgId, out var g) ? g : Guid.Empty;

    // Returns true if the caller has management access to the given SA.
    private async Task<bool> CanAccessAsync(ServiceAccount sa)
    {
        return Level switch
        {
            ManagementLevel.SuperAdmin   => true,
            ManagementLevel.OrgAdmin     => sa.UserList.OrgId != null && sa.UserList.OrgId == CallerOrgId,
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
            query = query.Where(sa => sa.UserList.OrgId != null && sa.UserList.OrgId == CallerOrgId);
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
        var patHashes = await db.PersonalAccessTokens
            .Where(p => p.ServiceAccountId == id)
            .Select(p => p.TokenHash)
            .ToListAsync();
        foreach (var hash in patHashes)
            await patService.InvalidateAsync(hash);
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
        // A PAT with no expiry is a permanent credential on a service account whose grants the
        // caller may lose tomorrow. Absent or over-long requests are clamped, not rejected, so
        // existing callers keep working — with a bounded credential.
        var maxExpiry = DateTimeOffset.UtcNow.AddDays(appConfig.MaxPatLifetimeDays);
        if (body.ExpiresAt <= DateTimeOffset.UtcNow) return BadRequest(new { error = "expires_at_in_the_past" });
        var expiresAt = body.ExpiresAt is { } requested && requested < maxExpiry ? requested : maxExpiry;
        var (raw, pat) = await patService.GenerateAsync(id, body.Name, expiresAt, ActorId);
        await audit.RecordAsync(sa.UserList.OrgId, null, ActorId, "sa.pat.created", AuditSa, id.ToString(),
            new() { ["pat_id"] = pat.Id.ToString(), ["expires_at"] = pat.ExpiresAt?.ToString("O") ?? "never" });
        return Ok(new { pat.Id, pat.Name, token = raw, pat.ExpiresAt, message = "store_this_token_shown_once" });
    }

    [HttpDelete("{id}/pat/{patId}")]
    public async Task<IActionResult> RevokePat(Guid id, Guid patId)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        try { await patService.RevokePat(patId, id); }
        catch (KeyNotFoundException) { return NotFound(); }
        await audit.RecordAsync(sa.UserList.OrgId, null, ActorId, "sa.pat.revoked", AuditSa, id.ToString(),
            new() { ["pat_id"] = patId.ToString() });
        return NoContent();
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
        string clientId;
        try { clientId = await patService.AddKeyAsync(sa, body.Jwk); }
        catch (Exception ex) { return BadRequest(new { error = "hydra_error", detail = ex.Message }); }
        await audit.RecordAsync(sa.UserList.OrgId, null, ActorId, "sa.key.added", AuditSa, id.ToString());
        return Ok(new { client_id = clientId });
    }

    [HttpDelete("{id}/api-keys")]
    public async Task<IActionResult> RemoveApiKey(Guid id)
    {
        var sa = await db.ServiceAccounts.Include(sa => sa.UserList).FirstOrDefaultAsync(sa => sa.Id == id);
        if (sa == null || !await CanAccessAsync(sa)) return NotFound();
        await patService.RemoveKeyAsync(sa);
        await audit.RecordAsync(sa.UserList.OrgId, null, ActorId, "sa.key.removed", AuditSa, id.ToString());
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

        var authErr = ValidateRoleAssignment(body);
        if (authErr != null) return authErr;

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
        // Cached introspections still carry the old role set — drop them now.
        await patService.InvalidateServiceAccountAsync(id);
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
        await patService.InvalidateServiceAccountAsync(id);
        await audit.RecordAsync(role.OrgId, role.ProjectId, ActorId, "sa.role.removed",
            AuditSa, id.ToString(), new() { ["role"] = role.Role });
        return NoContent();
    }

    private IActionResult? ValidateRoleAssignment(AssignSaRoleRequest body)
    {
        var targetLevel = body.Role switch
        {
            Roles.SuperAdmin   => ManagementLevel.SuperAdmin,
            Roles.OrgAdmin     => ManagementLevel.OrgAdmin,
            Roles.ProjectAdmin => ManagementLevel.ProjectAdmin,
            _                  => ManagementLevel.None
        };
        if (targetLevel == ManagementLevel.None) return BadRequest(new { error = "unknown_role" });
        if (targetLevel < Level) return StatusCode(403, new { error = "insufficient_level_to_grant_this_role" });
        if (Level == ManagementLevel.ProjectAdmin
            && ValidateProjectAdminRoleAssignment(body) is { } projectErr) return projectErr;
        if (body.Role == Roles.OrgAdmin && body.OrgId == null)
            return BadRequest(new { error = "org_id_required_for_org_admin" });
        if (body.Role == Roles.ProjectAdmin && (body.OrgId == null || body.ProjectId == null))
            return BadRequest(new { error = "org_id_and_project_id_required_for_project_admin" });
        // One rule for every caller below SuperAdmin, not just OrgAdmin. The org id written here
        // becomes the credential's tenant scope at PatService.IntrospectAsync, which is what
        // IntrospectionController scopes introspection by — so a caller that can choose it
        // chooses the boundary that is supposed to contain it.
        if (Level != ManagementLevel.SuperAdmin && body.OrgId != CallerOrgId)
            return StatusCode(403, new { error = "org_mismatch" });
        return null;
    }

    private IActionResult? ValidateProjectAdminRoleAssignment(AssignSaRoleRequest body)
    {
        if (body.Role != Roles.ProjectAdmin)
            return StatusCode(403, new { error = "project_admin_can_only_assign_project_admin" });
        if (!Guid.TryParse(Claims.ProjectId, out var pId) || body.ProjectId != pId)
            return StatusCode(403, new { error = "project_mismatch" });
        if (body.OrgId == null || body.ProjectId == null)
            return BadRequest(new { error = "org_id_and_project_id_required_for_project_admin" });
        return null;
    }
}

public record CreateSaRequest(string Name, string? Description, [property: System.Text.Json.Serialization.JsonRequired] Guid UserListId);
public record GenerateSaPatRequest(string Name, DateTimeOffset? ExpiresAt);
public record SaApiKeyRequest([property: System.Text.Json.Serialization.JsonRequired] System.Text.Json.JsonElement Jwk);
public record AssignSaRoleRequest(string Role, Guid? OrgId, Guid? ProjectId);
