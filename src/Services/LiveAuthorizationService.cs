using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Models;

namespace RediensIAM.Services;

/// <summary>
/// Re-checks, against Keto, that a management level carried by a token is still granted.
///
/// <c>ext.roles</c> is a snapshot taken when the token was issued. Authorising from it alone
/// means a revoked role or a suspended organisation stays effective until the token expires.
/// Every privileged request therefore confirms the claimed level is still real.
///
/// The result is cached per (user, level) for <see cref="CacheTtlSeconds"/> so the check costs
/// one Keto round-trip per user per window, not one per request. That window is the maximum
/// revocation lag — token lifetime no longer bounds it.
/// </summary>
public sealed class LiveAuthorizationService(
    KetoService keto,
    RediensIamDbContext db,
    IDistributedCache cache,
    ILogger<LiveAuthorizationService> logger)
{
    /// <summary>Upper bound on how long a revoked role keeps working. Keep it short.</summary>
    public const int CacheTtlSeconds = 30;

    public async Task<bool> IsStillGrantedAsync(TokenClaims claims, ManagementLevel level)
    {
        if (level == ManagementLevel.None) return false;

        // Service-account tokens are not Keto-backed: PatService resolves their roles from the
        // database and re-checks the account + organisation on every introspection, so they are
        // already live.
        if (claims.IsServiceAccount) return true;

        var userId = claims.ParsedUserId;
        if (userId == Guid.Empty) return false;

        var cacheKey = $"authz:{userId}:{(int)level}";
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached != null) return cached == "1";

        bool granted;
        try
        {
            granted = await CheckAsync(userId, level, claims.OrgId);
        }
        catch (Exception ex)
        {
            // Fail closed. An unreachable Keto must not silently promote token claims back to
            // being the sole source of authority.
            logger.LogError(ex, "Live authorisation check failed for user {UserId} level {Level}", userId, level);
            return false;
        }

        await cache.SetStringAsync(cacheKey, granted ? "1" : "0",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheTtlSeconds) });
        return granted;
    }

    private async Task<bool> CheckAsync(Guid userId, ManagementLevel level, string orgIdClaim)
    {
        var subject = $"user:{userId}";
        return level switch
        {
            ManagementLevel.SuperAdmin => await keto.CheckAsync(
                Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, subject),

            // Prefer the org the token names; fall back to "admin of any org" when the token
            // carries no org (the admin console issues such tokens).
            ManagementLevel.OrgAdmin => Guid.TryParse(orgIdClaim, out var orgId)
                ? await keto.CheckAsync(Roles.KetoOrgsNamespace, orgId.ToString(), Roles.KetoOrgAdminRelation, subject)
                : await keto.HasAnyRelationAsync(Roles.KetoOrgsNamespace, Roles.KetoOrgAdminRelation, subject),

            // project_admin has two grant paths and both are authoritative: a Keto manager
            // relation on a project (what GetConsent reads to put the role in the token) and an
            // org_roles row (what KetoService.GetActorManagementLevelForOrgAsync reads). Checking
            // only Keto denied admins who were granted the role the other way.
            ManagementLevel.ProjectAdmin =>
                await keto.HasAnyRelationAsync(Roles.KetoProjectsNamespace, Roles.KetoManagerRelation, subject)
                || await db.OrgRoles.AnyAsync(r => r.UserId == userId && r.Role == Roles.ProjectAdmin),

            _ => false,
        };
    }

    /// <summary>Drops the cached decisions for a user. Call when their roles change.</summary>
    public async Task InvalidateAsync(Guid userId)
    {
        foreach (var level in (ManagementLevel[])[ManagementLevel.SuperAdmin, ManagementLevel.OrgAdmin, ManagementLevel.ProjectAdmin])
            await cache.RemoveAsync($"authz:{userId}:{(int)level}");
    }
}
