using System.Security.Cryptography;
using System.Text;

namespace RediensIAM.Services;

public class BreachCheckService(IHttpClientFactory http, ILogger<BreachCheckService> logger)
{
    public async Task<int> GetBreachCountAsync(string password)
    {
        var sha1 = SHA1.HashData(Encoding.UTF8.GetBytes(password)); // NOSONAR: SHA1 is mandated by the HIBP k-anonymity API protocol
        var hex    = Convert.ToHexString(sha1).ToUpperInvariant();
        var prefix = hex[..5];
        var suffix = hex[5..];

        try
        {
            using var client = http.CreateClient();
            client.DefaultRequestHeaders.Add("Add-Padding", "true");
            var resp = await client.GetStringAsync(
                $"https://api.pwnedpasswords.com/range/{prefix}");

            foreach (var line in resp.Split('\n'))
            {
                var parts = line.Split(':');
                if (parts.Length >= 2 && parts[0].TrimEnd() == suffix)
                    return int.TryParse(parts[1].Trim(), out var count) ? count : 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HIBP breach check failed — allowing password");
            return 0; // fail open so outages don't block users
        }
    }
}
