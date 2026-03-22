using System.Security.Cryptography;
using Konscious.Security.Cryptography;

namespace RediensIAM.Services;

public class PasswordService(IConfiguration config)
{
    private readonly int _timeCost = config.GetValue<int>("Security:ArgonTimeCost", 3);
    private readonly int _memoryCost = config.GetValue<int>("Security:ArgonMemoryCost", 65536);
    private readonly int _parallelism = config.GetValue<int>("Security:ArgonParallelism", 4);

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
