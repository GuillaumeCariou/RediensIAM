using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using RediensIAM.Config;

namespace RediensIAM.Services;

public class PasswordService(AppConfig appConfig)
{
    private readonly int _timeCost = appConfig.ArgonTimeCost;
    private readonly int _memoryCost = appConfig.ArgonMemoryCost;
    private readonly int _parallelism = appConfig.ArgonParallelism;

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

    private byte[] Argon2Hash(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password));
        argon2.Salt = salt;
        argon2.DegreeOfParallelism = _parallelism;
        argon2.MemorySize = _memoryCost;
        argon2.Iterations = _timeCost;
        return argon2.GetBytes(32);
    }
}
