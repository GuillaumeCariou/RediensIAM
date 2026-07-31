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
    private readonly IReadOnlyList<(int Id, byte[] Pepper)> _peppers = appConfig.Argon2PepperRing;

    /// <summary>
    /// Pepper id assumed for a stored hash carrying no pepper marker: every hash written before
    /// S-10 used the single configured pepper, which is pepper id 1. If no pepper id 1 is
    /// configured, an unmarked hash was written with no pepper at all.
    /// </summary>
    private const int LegacyPepperId = 1;
    /// <summary>Pepper id meaning "no pepper".</summary>
    private const int NoPepperId = 0;
    private const string PepperMarker = "$k=";

    /// <summary>Pepper id every new hash is written under. 0 when no pepper is configured.</summary>
    public int ActivePepperId => _peppers.Count == 0 ? NoPepperId : _peppers[0].Id;

    private byte[] ActivePepper => _peppers.Count == 0 ? [] : _peppers[0].Pepper;

    /// <summary>
    /// Pepper bytes for a stored id. Returns false when the id is not configured — the operator
    /// dropped a pepper that still has hashes under it, and those users cannot authenticate.
    /// Fail closed and loudly rather than silently hashing with the wrong pepper.
    /// </summary>
    private bool TryGetPepper(int id, out byte[] pepper)
    {
        if (id == NoPepperId) { pepper = []; return true; }
        foreach (var (pid, p) in _peppers)
            if (pid == id) { pepper = p; return true; }
        // An unmarked hash with no pepper id 1 configured was written pepper-less.
        if (id == LegacyPepperId) { pepper = []; return true; }
        pepper = [];
        return false;
    }

    // Cached dummy stored hash for timing-equalisation on user-not-found in login paths.
    private string? _dummyHash;
    private readonly Lock _dummyLock = new();

    /// <summary>
    /// Marker appended to the PHC string naming the pepper the hash was derived under.
    /// Deliberately omitted for <see cref="LegacyPepperId"/> and <see cref="NoPepperId"/>, so a
    /// deployment that has never rotated its pepper writes the exact string format it wrote
    /// before. Absence of the marker is read as <see cref="LegacyPepperId"/>.
    /// </summary>
    private string PepperSuffix() =>
        ActivePepperId is NoPepperId or LegacyPepperId ? "" : $"{PepperMarker}{ActivePepperId}";

    /// <summary>Pepper id a stored hash was derived under.</summary>
    public static int PepperIdOf(string storedHash)
    {
        var idx = storedHash.LastIndexOf(PepperMarker, StringComparison.Ordinal);
        if (idx < 0) return LegacyPepperId;
        return int.TryParse(storedHash.AsSpan(idx + PepperMarker.Length), out var id) && id >= 0
            ? id
            : LegacyPepperId;
    }

    /// <summary>
    /// True when <paramref name="storedHash"/> is not under the active pepper. Call after a
    /// successful <see cref="Verify"/> — that is the only moment the plaintext exists — and
    /// re-<see cref="Hash"/> the password. Pepper rotation has no other migration path.
    /// </summary>
    public bool NeedsRepepper(string storedHash)
    {
        var stored = PepperIdOf(storedHash);
        if (stored == ActivePepperId) return false;
        // With no pepper configured, an unmarked hash already *is* the active (pepper-less)
        // state — it only reads as "pepper 1" so that peppered deployments stay compatible.
        return !(ActivePepperId == NoPepperId && stored == LegacyPepperId);
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Argon2Hash(password, salt, ActivePepper);
        return $"$argon2id$v=19$m={_memoryCost},t={_timeCost},p={_parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}{PepperSuffix()}";
    }

    public bool Verify(string password, string storedHash)
    {
        try
        {
            if (!TryGetPepper(PepperIdOf(storedHash), out var pepper)) return false;
            var parts = storedHash.Split('$');
            if (parts.Length < 6) return false;
            var salt = Convert.FromBase64String(parts[4]);
            // parts[5] is the hash, possibly followed by the pepper marker as parts[6].
            var expected = Convert.FromBase64String(parts[5]);
            var actual = Argon2Hash(password, salt, pepper);
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
    /// Stored format: <c>sha256:{keyId}:{hex}</c>. keyId is <c>p</c> when pepper id 1 is active
    /// (the pre-rotation marker, kept so nothing about the stored format changes until a pepper
    /// is actually rotated), <c>0</c> when no pepper is configured, and the numeric pepper id
    /// otherwise. Embedding the key id lets us reject hashes produced under a different key —
    /// silently verifying them would yield false negatives after a pepper is enabled or rotated.
    /// </summary>
    public string HashBackupCode(string code)
    {
        var (keyId, key) = ActiveBackupCodeKey();
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(code));
        return $"sha256:{keyId}:{Convert.ToHexString(mac)}";
    }

    /// <summary>
    /// Verifies a backup code against its stored hash. Supports the sha256:{keyId}:{hex} format
    /// (keyId <c>p</c> = pepper id 1, <c>0</c> = no pepper, or a numeric pepper id), the previous
    /// keyId-less sha256:{hex} format (treated as pepper-less), and legacy argon2id hashes
    /// (forwarded to <see cref="Verify"/>).
    /// </summary>
    public bool VerifyBackupCode(string submitted, string storedHash)
    {
        if (storedHash.StartsWith("sha256:", StringComparison.Ordinal))
        {
            if (!TryResolveBackupCodeKey(storedHash[7..], out var storedHex, out var keyForHash)) return false;
            var expected = Convert.FromHexString(storedHex);
            var actual = HMACSHA256.HashData(keyForHash, Encoding.UTF8.GetBytes(submitted));
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        return Verify(submitted, storedHash);
    }

    /// <summary>
    /// Splits the part of a stored backup-code hash after <c>sha256:</c> into its hex digest and
    /// the key that must have produced it. Returns false when the key id names a pepper that is
    /// not configured — the fail-closed case.
    /// </summary>
    private bool TryResolveBackupCodeKey(string rest, out string storedHex, out byte[] keyForHash)
    {
        storedHex  = "";
        keyForHash = [];
        // Format variants:
        //   sha256:{keyId}:{hex} → versioned format
        //   sha256:{hex}         → legacy unversioned (always pepper-less)
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var keyId = rest[..colon];
            storedHex = rest[(colon + 1)..];
            if (keyId == "0")
                keyForHash = DefaultBackupCodeKey;
            else
            {
                // "p" is the pre-rotation marker for pepper id 1; anything else is a numeric id.
                var pepperId = -1;
                if (keyId == "p") pepperId = LegacyPepperId;
                else if (int.TryParse(keyId, out var n)) pepperId = n;
                // Fail closed: a code stored under a pepper that is no longer configured
                // must not silently fall back to the unpeppered key.
                if (pepperId < 1 || !TryGetPepperStrict(pepperId, out keyForHash)) return false;
            }
        }
        else
        {
            storedHex = rest;
            keyForHash = DefaultBackupCodeKey;
        }
        return true;
    }

    /// <summary>Pepper lookup with no legacy fallback — used where a missing pepper must fail.</summary>
    private bool TryGetPepperStrict(int id, out byte[] pepper)
    {
        foreach (var (pid, p) in _peppers)
            if (pid == id) { pepper = p; return true; }
        pepper = [];
        return false;
    }

    private (string KeyId, byte[] Key) ActiveBackupCodeKey() => ActivePepperId switch
    {
        NoPepperId      => ("0", DefaultBackupCodeKey),
        LegacyPepperId  => ("p", ActivePepper),
        var id          => (id.ToString(System.Globalization.CultureInfo.InvariantCulture), ActivePepper),
    };

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

    private byte[] Argon2Hash(string password, byte[] salt, byte[] pepper)
    {
        // If a pepper is configured, mix it into the password via HMAC-SHA256.
        // Existing hashes (no pepper) keep verifying because pepper is empty by default.
        var input = pepper.Length == 0
            ? Encoding.UTF8.GetBytes(password)
            : HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(password));
        using var argon2 = new Argon2id(input);
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = _parallelism;
        argon2.MemorySize = _memoryCost;
        argon2.Iterations = _timeCost;
        return argon2.GetBytes(32);
    }
}
