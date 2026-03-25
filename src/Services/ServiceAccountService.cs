using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Entities;

namespace RediensIAM.Services;

// Shared SA key (Hydra JWK) and PAT operations used by both SystemAdminController and OrgController.
public class ServiceAccountService(RediensIamDbContext db)
{
    // ── Keys ──────────────────────────────────────────────────────────────────

    public async Task<object> GetKeysAsync(ServiceAccount sa, HydraAdminService hydra)
    {
        if (sa.HydraClientId == null)
            return new { client_id = (string?)null, has_key = false };

        var client = await hydra.GetOAuth2ClientAsync(sa.HydraClientId);
        if (client is null)
            return new { client_id = sa.HydraClientId, has_key = false };

        var hasJwks = client.Value.TryGetProperty("jwks", out var jwks)
            && jwks.TryGetProperty("keys", out var keys)
            && keys.GetArrayLength() > 0;
        var kid = hasJwks && jwks.TryGetProperty("keys", out var ks) && ks.GetArrayLength() > 0
            ? ks[0].TryGetProperty("kid", out var k) ? k.GetString() : null : null;

        return new { client_id = sa.HydraClientId, has_key = hasJwks, kid };
    }

    public async Task<string> AddKeyAsync(ServiceAccount sa, JsonElement jwk, HydraAdminService hydra)
    {
        var clientId = $"sa_{sa.Id}";
        await hydra.CreateOrUpdateServiceAccountClientAsync(clientId, sa.Name, jwk);
        sa.HydraClientId = clientId;
        await db.SaveChangesAsync();
        return clientId;
    }

    public async Task RemoveKeyAsync(ServiceAccount sa, HydraAdminService hydra)
    {
        if (sa.HydraClientId == null) return;
        await hydra.DeleteOAuth2ClientAsync(sa.HydraClientId);
        sa.HydraClientId = null;
        await db.SaveChangesAsync();
    }

    // ── PATs ──────────────────────────────────────────────────────────────────

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
        db.PersonalAccessTokens.Remove(pat);
        await db.SaveChangesAsync();
    }
}
