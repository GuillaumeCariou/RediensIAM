using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using StackExchange.Redis;

namespace RediensIAM.Config;

/// <summary>
/// TLS for the cache connection (R-15 / step 18 item A-3).
///
/// <para>
/// <c>ConnectionMultiplexer.ConnectAsync(string)</c> validates the server certificate against the
/// OS trust store, and no connection-string keyword redirects that. The cluster's cert-manager
/// root is not in the OS store, which is exactly where step 18 stopped:
/// <c>AuthenticationException … UntrustedRoot</c>. The fix is a
/// <see cref="ConfigurationOptions.CertificateValidation"/> callback.
/// </para>
///
/// <para>
/// A callback that returns <c>true</c> is worse than plaintext — it looks encrypted and accepts
/// any certificate an interceptor cares to present. <see cref="PinnedTo"/> is the opposite: the
/// server certificate must chain to a root in the mounted bundle and its name must match the
/// endpoint the connection string names. Nothing else is accepted, including a certificate the OS
/// trust store happens to trust — pinning that also honours the public WebPKI is not pinning.
/// </para>
///
/// <para>
/// StackExchange.Redis ships <c>ConfigurationOptions.TrustIssuer(path)</c>, which was considered
/// and not used: it returns <c>true</c> whenever <see cref="SslPolicyErrors"/> is
/// <see cref="SslPolicyErrors.None"/>, so the mounted root stops being a requirement the moment the
/// cache is moved to a publicly-certifiable hostname, and its callback is not reachable from a
/// test.
/// </para>
/// </summary>
public static class CacheTls
{
    /// <summary>
    /// Where the issuing CA is expected to be mounted. A path rather than a value so nothing has
    /// to be configured at install time: the chart mounts the cert-manager secret's
    /// <c>ca.crt</c> here, and if it is absent this whole file is inert.
    /// </summary>
    public const string DefaultCaBundlePath = "/etc/cache-tls/ca.crt";

    /// <summary>Server-authentication EKU. A client certificate must not open a server connection.</summary>
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Parses <paramref name="connectionString"/> and, when it asks for TLS and a CA bundle is
    /// present, pins server validation to that bundle.
    ///
    /// <para>
    /// Plaintext connection strings come back untouched, so this is a no-op on every deployment
    /// that has not turned cache TLS on. When TLS is on and the bundle is missing, validation is
    /// left at the .NET default — which is not a downgrade, it simply cannot trust a cluster root,
    /// and the connection fails loudly at startup exactly as it does today.
    /// </para>
    /// </summary>
    /// <param name="connectionString">The cache DSN, exactly as configured.</param>
    /// <param name="caBundlePath">PEM bundle of the root(s) to pin to. Absent = nothing to pin to.</param>
    /// <param name="log">
    /// Startup diagnostics. Both outcomes are reported — "pinned to N root(s)" is the operator's
    /// only evidence that the CA mount actually landed, and a silent success here is
    /// indistinguishable from a silent fallback.
    /// </param>
    public static ConfigurationOptions BuildOptions(string connectionString, string? caBundlePath, Action<string>? log = null)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        if (!options.Ssl) return options;

        if (string.IsNullOrWhiteSpace(caBundlePath) || !File.Exists(caBundlePath))
        {
            log?.Invoke(
                $"WARNING: cache TLS is enabled but no CA bundle was found at '{caBundlePath}'. " +
                "The server certificate will be checked against the OS trust store, which does not " +
                "contain a cluster-issued root — mount the issuing CA there.");
            return options;
        }

        var roots = LoadRoots(caBundlePath);
        options.CertificateValidation += PinnedTo(roots);
        log?.Invoke($"Cache TLS: server certificate pinned to {roots.Count} root(s) from '{caBundlePath}'.");
        return options;
    }

    private static X509Certificate2Collection LoadRoots(string caBundlePath)
    {
        var roots = new X509Certificate2Collection();
        try
        {
            roots.ImportFromPemFile(caBundlePath);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Cache TLS CA bundle '{caBundlePath}' is not readable as PEM.", ex);
        }
        if (roots.Count == 0)
            throw new InvalidOperationException(
                $"Cache TLS CA bundle '{caBundlePath}' contains no certificate. Refusing to start " +
                "rather than fall back to validation that cannot succeed.");
        return roots;
    }

    /// <summary>
    /// Accepts a server certificate only when it chains to one of <paramref name="roots"/>.
    /// Everything else is refused.
    /// </summary>
    public static RemoteCertificateValidationCallback PinnedTo(X509Certificate2Collection roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return (_, certificate, chain, errors) =>
        {
            // Chain trust is the only thing this callback is allowed to overrule.
            // RemoteCertificateNameMismatch stays fatal — a certificate the same cluster CA issued
            // for a different service must not open this connection — and
            // RemoteCertificateNotAvailable means there was nothing to check in the first place.
            if ((errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
            if (certificate is null) return false;

            using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var verifier = new X509Chain();
            verifier.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            verifier.ChainPolicy.CustomTrustStore.AddRange(roots);
            // cert-manager publishes neither a CRL nor an OCSP responder, so revocation checking
            // would fail every handshake. Short-lived certificates and rotation are the revocation
            // story here; this is a stated ceiling, not an oversight.
            verifier.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            verifier.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthOid));
            if (chain is not null)
                foreach (var element in chain.ChainElements)
                    verifier.ChainPolicy.ExtraStore.Add(element.Certificate);

            return verifier.Build(leaf);
        };
    }
}
