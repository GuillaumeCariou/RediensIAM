using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using RediensIAM.Data;
using RediensIAM.Models;
using Microsoft.EntityFrameworkCore;

namespace RediensIAM.Services;

public class PatIntrospectionService(
    RediensIamDbContext db,
    IConnectionMultiplexer redis,
    IConfiguration config)
{
    private readonly IDatabase _cache = redis.GetDatabase();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(config.GetValue<int>("Cache:PatTtlMinutes", 5));

    public async Task<IntrospectionResponse?> IntrospectAsync(string token)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var cacheKey = $"pat:{hash[..16]}";

        var cached = await _cache.StringGetAsync(cacheKey);
        if (!cached.IsNull)
            return JsonSerializer.Deserialize<IntrospectionResponse>(cached.ToString());

        var pat = await db.PersonalAccessTokens
            .Include(p => p.ServiceAccount)
                .ThenInclude(sa => sa.ProjectRoles)
            .FirstOrDefaultAsync(p => p.TokenHash == hash);

        if (pat == null || !pat.ServiceAccount.Active) return null;
        if (pat.ExpiresAt.HasValue && pat.ExpiresAt < DateTimeOffset.UtcNow) return null;

        pat.LastUsedAt = DateTimeOffset.UtcNow;
        pat.ServiceAccount.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Resolve context from the SA's UserList → Org
        var userList = await db.UserLists
            .FirstOrDefaultAsync(ul => ul.Id == pat.ServiceAccount.UserListId);

        IntrospectionResponse result;
        if (userList?.OrgId == null)
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
            var roles = pat.ServiceAccount.ProjectRoles.Select(r => r.Role).ToList();
            result = new IntrospectionResponse(
                Active: true,
                Sub: $"sa:{pat.ServiceAccount.Id}",
                OrgId: userList.OrgId.ToString()!,
                ProjectId: pat.ServiceAccount.ProjectRoles.FirstOrDefault()?.ProjectId.ToString() ?? "",
                Roles: roles,
                IsServiceAccount: true);
        }

        await _cache.StringSetAsync(cacheKey, JsonSerializer.Serialize(result), _ttl);
        return result;
    }

    public async Task InvalidateAsync(string tokenHash)
    {
        await _cache.KeyDeleteAsync($"pat:{tokenHash[..16]}");
    }
}
