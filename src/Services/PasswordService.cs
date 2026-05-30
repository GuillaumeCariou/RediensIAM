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

    /// <summary>
    /// Fast HMAC-SHA256 of a backup code. Backup codes are 8 random hex chars (~32 bits each)
    /// — the brute-force cost is bounded by rate limiting, not by per-hash work, so Argon2 is
    /// unnecessary and would amplify a DoS pivot during a brute-force attempt.
    /// </summary>
    public string HashBackupCode(string code)
    {
        var key = _pepper.Length > 0 ? _pepper : Encoding.UTF8.GetBytes("rediensiam-backup-code-v1");
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(code));
        return "sha256:" + Convert.ToHexString(mac);
    }

    /// <summary>
    /// Verifies a backup code against its stored hash. Supports the new sha256: format and
    /// legacy argon2id hashes (forwarded to <see cref="Verify"/>).
    /// </summary>
    public bool VerifyBackupCode(string submitted, string storedHash)
    {
        if (storedHash.StartsWith("sha256:", StringComparison.Ordinal))
        {
            var expected = Convert.FromHexString(storedHash[7..]);
            var actual = Convert.FromHexString(HashBackupCode(submitted)[7..]);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        return Verify(submitted, storedHash);
    }

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
