using System.Security.Cryptography;
using RediensIAM.Config;

namespace RediensIAM.Services;

public class TotpEncryptionService(AppConfig appConfig)
{
    private readonly byte[] _key = Convert.FromHexString(appConfig.TotpSecretEncryptionKey);

    public string Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public byte[] Decrypt(string encrypted)
    {
        var data = Convert.FromBase64String(encrypted);
        var nonce = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
