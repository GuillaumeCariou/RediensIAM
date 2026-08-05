using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

public class PatService(
    RediensIamDbContext db,
    PostgresSharedState state,
    AppConfig appConfig,
    IServiceScopeFactory scopeFactory,
    HydraService hydra,
    ILogger<PatService> logger)
{
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(appConfig.PatCacheTtlMinutes);
    private readonly string _prefix = appConfig.PatPrefix;

    // ── Generation ────────────────────────────────────────────────────────────

    public async Task<(string RawToken, PersonalAccessToken Pat)> GenerateAsync(
        Guid serviceAccountId, string name, DateTimeOffset? expiresAt, Guid? createdBy)
    {
        var raw = _prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(40))
            .Replace("+", "a").Replace("/", "b").Replace("=", "c");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        var pat = new PersonalAccessToken
        {
            ServiceAccountId = serviceAccountId,
            Name = name,
            TokenHash = hash,
            ExpiresAt = expiresAt,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PersonalAccessTokens.Add(pat);
        await db.SaveChangesAsync();
        return (raw, pat);
    }

    // ── Introspection ─────────────────────────────────────────────────────────

    public async Task<IntrospectionResponse?> IntrospectAsync(string token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var cacheKey = $"pat:{hash}";

        var cached = await state.GetStringAsync(cacheKey);
        if (cached is not null)
        {
            var hit = JsonSerializer.Deserialize<IntrospectionResponse>(cached);
            // The cache exists to skip the expensive join, never the authorisation decision.
            // Re-check liveness on every call so deactivating a service account or suspending
            // an organisation cuts access immediately instead of after the TTL.
            // Expiry is re-checked here too: IsStillLiveAsync covers the account and the org,
            // but a PAT that expired while this entry was warm would otherwise keep working for
            // the rest of the TTL.
            if (hit != null && !IsExpired(hit) && await IsStillLiveAsync(hit.Sub)) return hit;
            await state.RemoveAsync(cacheKey);
            return null;
        }

        var pat = await db.PersonalAccessTokens
            .Include(p => p.ServiceAccount)
                .ThenInclude(sa => sa.UserList)
                    .ThenInclude(ul => ul!.Organisation)
            .Include(p => p.ServiceAccount)
                .ThenInclude(sa => sa.Roles)
            .FirstOrDefaultAsync(p => p.TokenHash == hash);

        if (pat == null || !pat.ServiceAccount.Active) return null;
        if (pat.ExpiresAt.HasValue && pat.ExpiresAt < DateTimeOffset.UtcNow) return null;
        if (pat.ServiceAccount.UserList?.Organisation != null && !pat.ServiceAccount.UserList.Organisation.Active)
            return null;

        // Fire-and-forget: update LastUsedAt without blocking the auth path
        var patId = pat.Id;
        var saId  = pat.ServiceAccount.Id;
        var now   = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var bgDb = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
                await bgDb.PersonalAccessTokens.Where(p => p.Id == patId)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastUsedAt, now));
                await bgDb.ServiceAccounts.Where(sa => sa.Id == saId)
                    .ExecuteUpdateAsync(s => s.SetProperty(sa => sa.LastUsedAt, now));
            }
            catch (Exception ex) { logger.LogWarning(ex, "PAT LastUsedAt update failed for pat={PatId} sa={SaId}", patId, saId); }
        });

        var sa = pat.ServiceAccount;
        var saRoles = sa.Roles.ToList();

        // Pick the most privileged role to determine the token's org/project context.
        // Order: super_admin(1) > org_admin(2) > project_admin(3).
        var topRole = saRoles
            .OrderBy(r => r.Role switch
            {
                var x when x == RediensIAM.Config.Roles.SuperAdmin   => 1,
                var x when x == RediensIAM.Config.Roles.OrgAdmin     => 2,
                var x when x == RediensIAM.Config.Roles.ProjectAdmin => 3,
                _ => 99
            })
            .FirstOrDefault();

        var orgId     = topRole?.OrgId?.ToString() ?? sa.UserList?.OrgId?.ToString() ?? "";
        var projectId = topRole?.ProjectId?.ToString() ?? "";

        var result = new IntrospectionResponse(
            Active: true,
            Sub: $"sa:{sa.Id}",
            OrgId: orgId,
            ProjectId: projectId,
            Roles: saRoles.Select(r => r.Role).Distinct().ToList(),
            IsServiceAccount: true,
            ExpiresAt: pat.ExpiresAt);

        // A zero TTL means the operator asked for no cache at all — revocation immediate.
        if (_ttl > TimeSpan.Zero)
            await state.SetStringAsync(cacheKey, JsonSerializer.Serialize(result),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _ttl });
        return result;
    }

    public async Task InvalidateAsync(string tokenHash)
    {
        await state.RemoveAsync($"pat:{tokenHash}");
    }

    /// <summary>
    /// Drops every cached introspection for a service account. Call after any change to its
    /// roles — liveness is re-checked per request, but the role set is not.
    /// </summary>
    public async Task InvalidateServiceAccountAsync(Guid serviceAccountId)
    {
        var hashes = await db.PersonalAccessTokens
            .Where(p => p.ServiceAccountId == serviceAccountId)
            .Select(p => p.TokenHash)
            .ToListAsync();
        foreach (var hash in hashes)
            await state.RemoveAsync($"pat:{hash}");
    }

    private static bool IsExpired(IntrospectionResponse hit) =>
        hit.ExpiresAt.HasValue && hit.ExpiresAt < DateTimeOffset.UtcNow;

    /// <summary>
    /// Cheap per-request liveness probe for a cached PAT: the service account must still exist
    /// and be active, and its organisation must not be suspended.
    /// </summary>
    private async Task<bool> IsStillLiveAsync(string sub)
    {
        // Sub is "sa:{guid}" — see the IntrospectionResponse built below.
        var raw = sub.StartsWith("sa:", StringComparison.Ordinal) ? sub[3..] : sub;
        if (!Guid.TryParse(raw, out var saId)) return false;

        var live = await db.ServiceAccounts
            .Where(sa => sa.Id == saId)
            .Select(sa => new
            {
                sa.Active,
                OrgActive = sa.UserList == null
                            || sa.UserList.Organisation == null
                            || sa.UserList.Organisation.Active,
            })
            .FirstOrDefaultAsync();

        return live is { Active: true, OrgActive: true };
    }

    // ── Service account keys (Hydra JWK) ──────────────────────────────────────

    public async Task<object> GetKeysAsync(ServiceAccount sa)
    {
        if (sa.HydraClientId == null)
            return new { client_id = (string?)null, has_key = false };

        var client = await hydra.GetOAuth2ClientAsync(sa.HydraClientId);
        if (client is null)
            return new { client_id = sa.HydraClientId, has_key = false };

        var hasJwks = client.Value.TryGetProperty("jwks", out var jwks)
            && jwks.TryGetProperty("keys", out var keys)
            && keys.GetArrayLength() > 0;
        string? kid = null;
        if (hasJwks && jwks.TryGetProperty("keys", out var ks) && ks.GetArrayLength() > 0)
            kid = ks[0].TryGetProperty("kid", out var k) ? k.GetString() : null;

        return new { client_id = sa.HydraClientId, has_key = hasJwks, kid };
    }

    public async Task<string> AddKeyAsync(ServiceAccount sa, JsonElement jwk)
    {
        var clientId = $"sa_{sa.Id}";
        await hydra.CreateOrUpdateServiceAccountClientAsync(clientId, sa.Name, jwk);
        sa.HydraClientId = clientId;
        await db.SaveChangesAsync();
        return clientId;
    }

    public async Task RemoveKeyAsync(ServiceAccount sa)
    {
        if (sa.HydraClientId == null) return;
        await hydra.DeleteOAuth2ClientAsync(sa.HydraClientId);
        sa.HydraClientId = null;
        await db.SaveChangesAsync();
    }

    // ── PAT management ────────────────────────────────────────────────────────

    public async Task<IEnumerable<object>> ListPatsAsync(Guid saId)
    {
        return await db.PersonalAccessTokens
            .Where(p => p.ServiceAccountId == saId)
            .Select(p => new { p.Id, p.Name, p.ExpiresAt, p.LastUsedAt, p.CreatedAt })
            .ToListAsync<object>();
    }

    public async Task RevokePat(Guid patId, Guid saId)
    {
        var pat = await db.PersonalAccessTokens.FirstOrDefaultAsync(p => p.Id == patId && p.ServiceAccountId == saId)
            ?? throw new KeyNotFoundException("PAT not found");
        await InvalidateAsync(pat.TokenHash);
        db.PersonalAccessTokens.Remove(pat);
        await db.SaveChangesAsync();
    }
}
