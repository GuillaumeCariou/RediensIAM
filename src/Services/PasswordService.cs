using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using RediensIAM.Config;

namespace RediensIAM.Services;

public class PasswordService(AppConfig appConfig)
{
    private readonly int _timeCost = appConfig.ArgonTimeCost;
    private readonly int _memoryCost = appConfig.ArgonMemoryCost;
    private readonly int _parallelism = appConfig.ArgonParallelism;
    private readonly byte[] _pepper = string.IsNullOrEmpty(appConfig.Argon2Pepper)
        ? []
        : Convert.FromHexString(appConfig.Argon2Pepper);

    // Cached dummy stored hash for timing-equalisation on user-not-found in login paths.
    private string? _dummyHash;
    private readonly Lock _dummyLock = new();

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Argon2Hash(password, salt);
        return $"$argon2id$v=19$m={_memoryCost},t={_timeCost},p={_parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string storedHash)
    {
        try
        {
            var parts = storedHash.Split('$');
            if (parts.Length < 6) return false;
            var salt = Convert.FromBase64String(parts[4]);
            var expected = Convert.FromBase64String(parts[5]);
            var actual = Argon2Hash(password, salt);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static readonly byte[] DefaultBackupCodeKey = Encoding.UTF8.GetBytes("rediensiam-backup-code-v1");

    /// <summary>
    /// Fast HMAC-SHA256 of a backup code. Backup codes are 8 random hex chars (~32 bits each)
    /// — the brute-force cost is bounded by rate limiting, not by per-hash work, so Argon2 is
    /// unnecessary and would amplify a DoS pivot during a brute-force attempt.
    ///
    /// Stored format: <c>sha256:{keyId}:{hex}</c> where keyId is <c>p</c> when a pepper is
    /// configured, otherwise <c>0</c>. Embedding the key id lets us reject hashes that were
    /// produced under a different key — silently verifying them would yield false negatives
    /// after a pepper is enabled or rotated.
    /// </summary>
    public string HashBackupCode(string code)
    {
        var (keyId, key) = ActiveBackupCodeKey();
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(code));
        return $"sha256:{keyId}:{Convert.ToHexString(mac)}";
    }

    /// <summary>
    /// Verifies a backup code against its stored hash. Supports the new sha256:{keyId}:{hex}
    /// format, the previous keyId-less sha256:{hex} format (treated as keyId=0 — pepper-less),
    /// and legacy argon2id hashes (forwarded to <see cref="Verify"/>).
    /// </summary>
    public bool VerifyBackupCode(string submitted, string storedHash)
    {
        if (storedHash.StartsWith("sha256:", StringComparison.Ordinal))
        {
            var rest = storedHash[7..];
            string storedHex;
            byte[] keyForHash;
            // Format variants:
            //   sha256:{keyId}:{hex} → new versioned format
            //   sha256:{hex}        → legacy unversioned (always pepper-less)
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var keyId = rest[..colon];
                storedHex = rest[(colon + 1)..];
                keyForHash = keyId == "p" ? _pepper : DefaultBackupCodeKey;
                // Mismatched keyId (e.g. stored under pepper, now no pepper) → cannot verify.
                if (keyId == "p" && _pepper.Length == 0) return false;
            }
            else
            {
                storedHex = rest;
                keyForHash = DefaultBackupCodeKey;
            }
            var expected = Convert.FromHexString(storedHex);
            var actual = HMACSHA256.HashData(keyForHash, Encoding.UTF8.GetBytes(submitted));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        return Verify(submitted, storedHash);
    }

    private (string KeyId, byte[] Key) ActiveBackupCodeKey() =>
        _pepper.Length > 0 ? ("p", _pepper) : ("0", DefaultBackupCodeKey);

    /// <summary>
    /// Performs the same Argon2 work as <see cref="Verify"/> against a cached dummy hash
    /// and always returns false. Call this on user-not-found in login paths to defeat
    /// timing-based user enumeration.
    /// </summary>
    public bool DummyVerify(string password)
    {
        if (_dummyHash == null)
        {
            lock (_dummyLock)
            {
                _dummyHash ??= Hash("__dummy_password_for_timing_equalisation__");
            }
        }
        Verify(password, _dummyHash);
        return false;
    }

    private byte[] Argon2Hash(string password, byte[] salt)
    {
        // If a pepper is configured, mix it into the password via HMAC-SHA256.
        // Existing hashes (no pepper) keep verifying because pepper is empty by default.
        var input = _pepper.Length == 0
            ? Encoding.UTF8.GetBytes(password)
            : HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(password));
        using var argon2 = new Argon2id(input);
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = _parallelism;
        argon2.MemorySize = _memoryCost;
        argon2.Iterations = _timeCost;
        return argon2.GetBytes(32);
    }
}
