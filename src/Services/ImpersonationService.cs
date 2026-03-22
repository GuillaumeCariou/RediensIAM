using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace RediensIAM.Services;

public record ImpersonationClaims(
    string UserId,
    string OrgId,
    string ProjectId,
    List<string> Roles,
    string ImpersonatedBy);

public class ImpersonationService(IConnectionMultiplexer redis)
{
    private const string Prefix = "rediens_imp_";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<string> CreateAsync(ImpersonationClaims claims)
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var token = Prefix + raw;
        var hash = Hash(token);
        var json = JsonSerializer.Serialize(claims);
        await _db.StringSetAsync($"imp:{hash}", json, Ttl);
        return token;
    }

    public async Task<ImpersonationClaims?> ResolveAsync(string token)
    {
        var hash = Hash(token);
        var val = await _db.StringGetAsync($"imp:{hash}");
        if (val.IsNull) return null;
        return JsonSerializer.Deserialize<ImpersonationClaims>(val.ToString());
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..32];
}
