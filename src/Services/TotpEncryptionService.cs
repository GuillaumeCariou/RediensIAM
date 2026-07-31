using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RediensIAM.Services;

/// <summary>
/// The set of per-purpose subkeys a value may be encrypted under: exactly one is active
/// (everything new is written under it), all of them can decrypt. This is what makes root-key
/// rotation incremental rather than a cutover — see <c>.security-hardening/16-key-rotation.md</c>.
/// Ordering follows Ory Hydra's convention: the first configured key is the active one.
/// </summary>
public sealed class KeyRing
{
    private readonly IReadOnlyDictionary<int, byte[]> _keys;

    public KeyRing(int activeId, IReadOnlyDictionary<int, byte[]> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (!keys.ContainsKey(activeId))
            throw new ArgumentException($"Active key id {activeId} is not present in the key ring.", nameof(activeId));
        ActiveId = activeId;
        _keys = keys;
    }

    /// <summary>Convenience ctor for a single-key ring — the pre-rotation shape.</summary>
    public KeyRing(int id, byte[] key) : this(id, new Dictionary<int, byte[]> { [id] = key }) { }

    public int ActiveId { get; }
    public byte[] ActiveKey => _keys[ActiveId];
    public IReadOnlyCollection<int> KeyIds => (IReadOnlyCollection<int>)_keys.Keys;

    public byte[] KeyFor(int id) => _keys.TryGetValue(id, out var k)
        ? k
        : throw new CryptographicException(
            $"Ciphertext was encrypted under key id {id}, which is not configured. " +
            "Re-add it to Security:EncryptionKeys — dropping a key that still has data under it is unrecoverable.");
}

public static class TotpEncryption
{
    private const string ProvidersKey       = "providers";
    private const string ClientSecretEncKey = "client_secret_enc";

    /// <summary>
    /// Key id assumed for a stored value that carries no key-id prefix. Every ciphertext written
    /// before key rotation existed is, by definition, under the one and only root key — so the
    /// absence of a prefix means "key 1", not "unknown". This is the backward-compatibility rule
    /// the whole migration rests on; do not change it.
    /// </summary>
    public const int LegacyKeyId = 1;

    /// <summary>
    /// Envelope prefix for a key id. Deliberately empty for <see cref="LegacyKeyId"/>: a
    /// deployment that has never rotated keeps writing the exact byte format it wrote before,
    /// so the format change is inert until an operator actually rotates.
    /// The Base64 alphabet contains no ':', so the prefix can never be confused with the body.
    /// </summary>
    private static string Prefix(int keyId) =>
        keyId == LegacyKeyId ? "" : $"k{keyId.ToString(CultureInfo.InvariantCulture)}:";

    /// <summary>Splits a stored value into (key id, base64 body). No prefix ⇒ <see cref="LegacyKeyId"/>.</summary>
    internal static (int KeyId, string Body) ParseEnvelope(string stored)
    {
        if (stored.Length > 2 && stored[0] == 'k')
        {
            var colon = stored.IndexOf(':', StringComparison.Ordinal);
            if (colon > 1 &&
                int.TryParse(stored.AsSpan(1, colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0)
                return (id, stored[(colon + 1)..]);
        }
        return (LegacyKeyId, stored);
    }

    /// <summary>The key id a stored value was encrypted under. Used by the re-encryption sweep.</summary>
    public static int KeyIdOf(string encrypted) => ParseEnvelope(encrypted).KeyId;

    /// <summary>Always encrypts under the ring's active key.</summary>
    public static string Encrypt(KeyRing ring, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(ring.ActiveKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Prefix(ring.ActiveId) + Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    /// <summary>
    /// Decrypts under whichever configured key the envelope names. Throws
    /// <see cref="CryptographicException"/> if that key is not configured — loudly, because the
    /// alternative is a silent authentication failure that looks like a corrupt secret.
    /// </summary>
    public static byte[] Decrypt(KeyRing ring, string encrypted)
    {
        var (keyId, body) = ParseEnvelope(encrypted);
        var data = Convert.FromBase64String(body);
        var nonce = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(ring.KeyFor(keyId), 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>
    /// Returns a copy of the theme with <c>client_secret</c> and <c>client_secret_enc</c>
    /// removed from every provider. Safe to include in API responses.
    /// </summary>
    public static Dictionary<string, object>? StripSecretsFromTheme(Dictionary<string, object>? theme)
    {
        if (theme == null) return null;
        if (!theme.TryGetValue(ProvidersKey, out var raw)) return theme;
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Array) return theme;

        var strippedProviders = el.EnumerateArray()
            .Select(p => p.EnumerateObject()
                .Where(prop => prop.Name != "client_secret" && prop.Name != ClientSecretEncKey)
                .ToDictionary(prop => prop.Name, prop => (object)prop.Value.Clone()))
            .ToList<object>();

        return new Dictionary<string, object>(theme) { [ProvidersKey] = strippedProviders };
    }

    public static string EncryptString(KeyRing ring, string plaintext)
        => Encrypt(ring, Encoding.UTF8.GetBytes(plaintext));

    public static string DecryptString(KeyRing ring, string encrypted)
        => Encoding.UTF8.GetString(Decrypt(ring, encrypted));

    /// <summary>
    /// Walks the "providers" array in a login_theme dictionary, encrypts any plaintext
    /// <c>client_secret</c> values into <c>client_secret_enc</c>, and preserves existing
    /// encrypted secrets when the caller omits the field (i.e. didn't change the secret).
    /// Returns a new dictionary — the inputs are never mutated.
    /// </summary>
    public static Dictionary<string, object>? EncryptProviderSecretsInTheme(
        Dictionary<string, object>? incoming,
        Dictionary<string, object>? existing,
        KeyRing ring)
    {
        if (incoming == null) return null;
        if (!incoming.TryGetValue(ProvidersKey, out var rawIn)) return incoming;
        if (rawIn is not JsonElement inEl || inEl.ValueKind != JsonValueKind.Array) return incoming;

        var existingSecrets = BuildExistingSecretsMap(existing);
        var updatedProviders = inEl.EnumerateArray()
            .Select(p => EncryptProviderEntry(p, existingSecrets, ring))
            .ToList<object>();

        return new Dictionary<string, object>(incoming) { [ProvidersKey] = updatedProviders };
    }

    /// <summary>
    /// Every <c>client_secret_enc</c> in a stored theme, with the key id it is encrypted under.
    /// Used by the re-encryption sweep to decide whether a project row needs rewriting.
    /// </summary>
    public static IEnumerable<int> ProviderSecretKeyIds(Dictionary<string, object>? theme)
    {
        if (theme?.TryGetValue(ProvidersKey, out var raw) != true) yield break;
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Array) yield break;
        foreach (var p in el.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Object &&
                p.TryGetProperty(ClientSecretEncKey, out var enc) &&
                enc.ValueKind == JsonValueKind.String &&
                enc.GetString() is { Length: > 0 } s)
                yield return KeyIdOf(s);
    }

    /// <summary>
    /// Decrypts every <c>client_secret_enc</c> in a stored theme and re-encrypts it under the
    /// ring's active key. Returns a new dictionary; the input is never mutated.
    /// </summary>
    public static Dictionary<string, object> ReEncryptProviderSecrets(
        Dictionary<string, object> theme, KeyRing ring)
    {
        if (!theme.TryGetValue(ProvidersKey, out var raw)) return theme;
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Array) return theme;

        var rewritten = el.EnumerateArray().Select(p =>
        {
            var dict = p.EnumerateObject().ToDictionary(prop => prop.Name, prop => (object)prop.Value.Clone());
            if (p.TryGetProperty(ClientSecretEncKey, out var enc) &&
                enc.ValueKind == JsonValueKind.String &&
                enc.GetString() is { Length: > 0 } s &&
                KeyIdOf(s) != ring.ActiveId)
                dict[ClientSecretEncKey] = EncryptString(ring, DecryptString(ring, s));
            return (object)dict;
        }).ToList();

        return new Dictionary<string, object>(theme) { [ProvidersKey] = rewritten };
    }

    private static Dictionary<string, string> BuildExistingSecretsMap(Dictionary<string, object>? existing)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existing?.TryGetValue(ProvidersKey, out var rawEx) != true) return map;
        if (rawEx is not JsonElement exEl || exEl.ValueKind != JsonValueKind.Array) return map;
        foreach (var p in exEl.EnumerateArray())
        {
            if (p.TryGetProperty("id", out var idProp) && idProp.GetString() is { } pid &&
                p.TryGetProperty(ClientSecretEncKey, out var encProp) && encProp.GetString() is { } enc)
                map[pid] = enc;
        }
        return map;
    }

    private static Dictionary<string, object> EncryptProviderEntry(
        JsonElement p, Dictionary<string, string> existingSecrets, KeyRing ring)
    {
        var dict = p.EnumerateObject()
            .Where(prop => prop.Name != "client_secret" && prop.Name != ClientSecretEncKey)
            .ToDictionary(prop => prop.Name, prop => (object)prop.Value.Clone());

        var providerId = p.TryGetProperty("id", out var idP) ? idP.GetString() : null;

        if (p.TryGetProperty("client_secret", out var csProp) && !string.IsNullOrEmpty(csProp.GetString()))
            dict[ClientSecretEncKey] = EncryptString(ring, csProp.GetString()!);
        else if (providerId != null && existingSecrets.TryGetValue(providerId, out var enc))
            dict[ClientSecretEncKey] = enc;

        return dict;
    }
}
