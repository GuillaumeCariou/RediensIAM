using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Models;
using Microsoft.EntityFrameworkCore;

namespace RediensIAM.Services;

public class PatIntrospectionService(
    RediensIamDbContext db,
    IConnectionMultiplexer redis,
    AppConfig appConfig,
    IServiceScopeFactory scopeFactory)
{
    private readonly IDatabase _cache = redis.GetDatabase();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(appConfig.PatCacheTtlMinutes);

    public async Task<IntrospectionResponse?> IntrospectAsync(string token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var cacheKey = $"pat:{hash[..32]}";

        var cached = await _cache.StringGetAsync(cacheKey);
        if (!cached.IsNull)
            return JsonSerializer.Deserialize<IntrospectionResponse>(cached.ToString());

        var pat = await db.PersonalAccessTokens
            .Include(p => p.ServiceAccount)
                .ThenInclude(sa => sa.ProjectRoles)
            .FirstOrDefaultAsync(p => p.TokenHash == hash);

        if (pat == null || !pat.ServiceAccount.Active) return null;
        if (pat.ExpiresAt.HasValue && pat.ExpiresAt < DateTimeOffset.UtcNow) return null;

        // Fire-and-forget: update LastUsedAt without blocking the auth path
        var patId = pat.Id;
        var saId = pat.ServiceAccount.Id;
        var now = DateTimeOffset.UtcNow;
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
            catch { /* non-critical */ }
        });

        // Resolve context from the SA's UserList → Org
        var userList = await db.UserLists
            .FirstOrDefaultAsync(ul => ul.Id == pat.ServiceAccount.UserListId);

        IntrospectionResponse result;
        if (pat.ServiceAccount.IsSystem)
        {
            // System SA — get roles from ServiceAccountOrgRole
            var sysRoles = await db.ServiceAccountOrgRoles
                .Where(r => r.ServiceAccountId == pat.ServiceAccount.Id)
                .Select(r => r.Role)
                .ToListAsync();
            result = new IntrospectionResponse(
                Active: true,
                Sub: $"sa:{pat.ServiceAccount.Id}",
                OrgId: "",
                ProjectId: "",
                Roles: sysRoles,
                IsServiceAccount: true);
        }
        else
        {
            var projectRoles = pat.ServiceAccount.ProjectRoles.ToList();
            var roles = projectRoles.Select(r => r.Role).ToList();
            result = new IntrospectionResponse(
                Active: true,
                Sub: $"sa:{pat.ServiceAccount.Id}",
                OrgId: userList?.OrgId.ToString() ?? "",
                ProjectId: projectRoles.Count == 1 ? projectRoles[0].ProjectId.ToString() : "",
                Roles: roles,
                IsServiceAccount: true);
        }

        await _cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(result), _ttl);
        return result;
    }

    public async Task InvalidateAsync(string tokenHash)
    {
        await _cache.KeyDeleteAsync($"pat:{tokenHash[..32]}");
    }
}
