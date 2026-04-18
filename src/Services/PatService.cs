using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Models;

namespace RediensIAM.Services;

public class PatService(
    RediensIamDbContext db,
    IConnectionMultiplexer redis,
    AppConfig appConfig,
    IServiceScopeFactory scopeFactory,
    HydraService hydra,
    ILogger<PatService> logger)
{
    private readonly IDatabase _cache = redis.GetDatabase();
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

        var cached = await _cache.StringGetAsync(cacheKey);
        if (!cached.IsNull)
            return JsonSerializer.Deserialize<IntrospectionResponse>(cached.ToString());

        var pat = await db.PersonalAccessTokens
            .Include(p => p.ServiceAccount)
                .ThenInclude(sa => sa.UserList)
            .Include(p => p.ServiceAccount)
                .ThenInclude(sa => sa.Roles)
            .FirstOrDefaultAsync(p => p.TokenHash == hash);

        if (pat == null || !pat.ServiceAccount.Active) return null;
        if (pat.ExpiresAt.HasValue && pat.ExpiresAt < DateTimeOffset.UtcNow) return null;

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

        var orgId     = topRole?.OrgId?.ToString() ?? sa.UserList.OrgId?.ToString() ?? "";
        var projectId = topRole?.ProjectId?.ToString() ?? "";

        var result = new IntrospectionResponse(
            Active: true,
            Sub: $"sa:{sa.Id}",
            OrgId: orgId,
            ProjectId: projectId,
            Roles: saRoles.Select(r => r.Role).Distinct().ToList(),
            IsServiceAccount: true);

        await _cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(result), _ttl);
        return result;
    }

    public async Task InvalidateAsync(string tokenHash)
    {
        await _cache.KeyDeleteAsync($"pat:{tokenHash}");
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
