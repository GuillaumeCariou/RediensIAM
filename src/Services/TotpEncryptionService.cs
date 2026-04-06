using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RediensIAM.Services;

public static class TotpEncryption
{
    private const string ProvidersKey       = "providers";
    private const string ClientSecretEncKey = "client_secret_enc";
    public static string Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public static byte[] Decrypt(byte[] key, string encrypted)
    {
        var data = Convert.FromBase64String(encrypted);
        var nonce = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, 16);
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

    public static string EncryptString(byte[] key, string plaintext)
        => Encrypt(key, Encoding.UTF8.GetBytes(plaintext));

    public static string DecryptString(byte[] key, string encrypted)
        => Encoding.UTF8.GetString(Decrypt(key, encrypted));

    /// <summary>
    /// Walks the "providers" array in a login_theme dictionary, encrypts any plaintext
    /// <c>client_secret</c> values into <c>client_secret_enc</c>, and preserves existing
    /// encrypted secrets when the caller omits the field (i.e. didn't change the secret).
    /// Returns a new dictionary — the inputs are never mutated.
    /// </summary>
    public static Dictionary<string, object>? EncryptProviderSecretsInTheme(
        Dictionary<string, object>? incoming,
        Dictionary<string, object>? existing,
        byte[] key)
    {
        if (incoming == null) return null;
        if (!incoming.TryGetValue(ProvidersKey, out var rawIn)) return incoming;
        if (rawIn is not JsonElement inEl || inEl.ValueKind != JsonValueKind.Array) return incoming;

        // Build map of existing encrypted secrets: providerId → client_secret_enc
        var existingSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existing?.TryGetValue(ProvidersKey, out var rawEx) == true &&
            rawEx is JsonElement exEl && exEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in exEl.EnumerateArray())
            {
                if (p.TryGetProperty("id", out var idProp) && idProp.GetString() is { } pid &&
                    p.TryGetProperty(ClientSecretEncKey, out var encProp) && encProp.GetString() is { } enc)
                    existingSecrets[pid] = enc;
            }
        }

        var updatedProviders = inEl.EnumerateArray().Select(p =>
        {
            // Copy all props except client_secret / client_secret_enc
            var dict = p.EnumerateObject()
                .Where(prop => prop.Name != "client_secret" && prop.Name != ClientSecretEncKey)
                .ToDictionary(prop => prop.Name, prop => (object)prop.Value.Clone());

            var providerId = p.TryGetProperty("id", out var idP) ? idP.GetString() : null;

            // New secret provided → encrypt it
            if (p.TryGetProperty("client_secret", out var csProp) &&
                !string.IsNullOrEmpty(csProp.GetString()))
            {
                dict[ClientSecretEncKey] = EncryptString(key, csProp.GetString()!);
            }
            // No new secret → preserve existing encrypted one if available
            else if (providerId != null && existingSecrets.TryGetValue(providerId, out var existingEnc))
            {
                dict[ClientSecretEncKey] = existingEnc;
            }

            return dict;
        }).ToList<object>();

        return new Dictionary<string, object>(incoming) { [ProvidersKey] = updatedProviders };
    }
}
