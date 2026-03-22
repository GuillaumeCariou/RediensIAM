using System.Security.Cryptography;
using System.Text;
using RediensIAM.Data;
using RediensIAM.Entities;

namespace RediensIAM.Services;

public class PatGenerationService(RediensIamDbContext db, IConfiguration config)
{
    private readonly string _prefix = config["Security:PatPrefix"] ?? "rediens_pat_";

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
}
