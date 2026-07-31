using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;

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
    HydraService hydra,
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

        // Every decision below super_admin is a function of claims.OrgId — the org-active check in
        // CheckAsync reads it — and project_admin is additionally a function of claims.ProjectId,
        // so a cached answer is only valid for the scope it was decided under. The scope travels
        // in the value rather than the key so InvalidateAsync can still drop every decision for a
        // user without enumerating orgs.
        var scope = level == ManagementLevel.SuperAdmin ? "" : $"{claims.OrgId}/{claims.ProjectId}";
        var cacheKey = $"authz:{userId}:{(int)level}";
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached != null && cached.Split('|', 2) is [var verdict, var cachedScope] && cachedScope == scope)
            return verdict == "1";

        bool granted;
        try
        {
            granted = await CheckAsync(userId, level, claims.OrgId, claims.ProjectId);
        }
        catch (Exception ex)
        {
            // Fail closed. An unreachable Keto must not silently promote token claims back to
            // being the sole source of authority.
            logger.LogError(ex, "Live authorisation check failed for user {UserId} level {Level}", userId, level);
            return false;
        }

        await cache.SetStringAsync(cacheKey, $"{(granted ? "1" : "0")}|{scope}",
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(CacheTtlSeconds) });
        return granted;
    }

    private async Task<bool> CheckAsync(Guid userId, ManagementLevel level, string orgIdClaim, string projectIdClaim)
    {
        // Suspension has to remove authority, not just sessions. `AdminLogin` consults no
        // organisation, so a system-list org_admin — one granted by AssignOrgAdmin on a user
        // outside the org — simply logs in again and their new token carries the surviving Keto
        // tuple. Checking here rather than at login covers every management request, whatever
        // minted the token. super_admin is exempt by construction: its branch below takes no org
        // id, and it is the level that unsuspends the org.
        // Narrow on purpose: a *suspended* org is refused, an org id that matches no row is left
        // to the controller, which answers 404. Turning "unknown org" into "forbidden" here would
        // move every not-found into the filter and tell a caller nothing it did not already know.
        if (level != ManagementLevel.SuperAdmin && Guid.TryParse(orgIdClaim, out var claimedOrg)
            && await db.Organisations.AnyAsync(o => o.Id == claimedOrg && !o.Active))
            return false;

        // S-8: one implementation, in KetoService. This method used to carry a second one — with
        // an unscoped `db.OrgRoles` fallback that answered "project_admin somewhere" to the
        // question "project_admin here" (R-22 residual 3).
        return await keto.IsManagementLevelGrantedAsync(
            userId, level,
            Guid.TryParse(orgIdClaim, out var orgId) ? orgId : null,
            Guid.TryParse(projectIdClaim, out var projectId) ? projectId : null);
    }

    /// <summary>
    /// Called when a user's management roles change. Drops the cached decisions AND revokes their
    /// management-console sessions.
    ///
    /// The cache drop only protects RediensIAM's own surface: the issued token still carries the
    /// old <c>ext.roles</c>, and a resource server validating it locally against JWKS honours the
    /// revoked role for the token's full lifetime. Revoking the Hydra session is the only way to
    /// end that window — the next request has to obtain a token minted from the new grants.
    ///
    /// The subject is the bare user id, which is what <c>AdminLogin</c> accepts the login with;
    /// tenant sessions (<c>{orgId}:{userId}</c>) carry project roles, not management ones, and are
    /// deliberately left alone.
    /// </summary>
    public async Task InvalidateAsync(Guid userId)
    {
        foreach (var level in (ManagementLevel[])[ManagementLevel.SuperAdmin, ManagementLevel.OrgAdmin, ManagementLevel.ProjectAdmin])
            await cache.RemoveAsync($"authz:{userId}:{(int)level}");

        try
        {
            await hydra.RevokeSessionsAsync(userId.ToString());
        }
        catch (Exception ex)
        {
            // The grant change is already committed; failing the request would misreport it. The
            // cache drop above still closes the window on this deployment's own surface.
            logger.LogWarning(ex, "Session revocation after a role change failed for user {UserId}", userId);
        }
    }
}
