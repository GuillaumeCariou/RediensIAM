using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RediensIAM.Config;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// R-15 / step 18 item A-3 — the cache's TLS certificate validation.
///
/// <para>
/// The whole risk of this change is one line: a <c>CertificateValidation</c> callback that returns
/// <c>true</c> looks like encryption and accepts any certificate a man in the middle presents.
/// These tests exist to prove the callback refuses everything except a certificate that chains to
/// the mounted cluster root, and they run entirely on certificates generated here — no container,
/// no cluster.
/// </para>
/// </summary>
public class CacheTlsPinningTests
{
    // ── The certificate that should be accepted ───────────────────────────────

    [Fact]
    public void Leaf_Issued_By_The_Pinned_Root_Is_Accepted()
    {
        using var ca = SelfSignedCa("CN=RediensIAM cluster CA");
        using var leaf = LeafSignedBy(ca, "cache.internal");

        Pin(ca)(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
    }

    [Fact]
    public void Self_Signed_Server_Certificate_Pinned_To_Itself_Is_Accepted()
    {
        // cert-manager's `selfsigned` issuer — the mode this deployment actually runs — publishes a
        // ca.crt identical to the leaf. A chain of length one still has to verify.
        using var selfSigned = SelfSignedServer("cache.internal");

        Pin(selfSigned)(this, selfSigned, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
    }

    // ── Everything that must be refused ───────────────────────────────────────

    [Fact]
    public void Leaf_Issued_By_A_Different_Root_Is_Refused()
    {
        // The man in the middle who runs his own CA. This is the case a `return true` callback
        // accepts and the reason this file exists.
        using var ourCa = SelfSignedCa("CN=RediensIAM cluster CA");
        using var attackerCa = SelfSignedCa("CN=Attacker CA");
        using var attackerLeaf = LeafSignedBy(attackerCa, "cache.internal");

        Pin(ourCa)(this, attackerLeaf, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    [Fact]
    public void Unrelated_Self_Signed_Certificate_Is_Refused()
    {
        using var ourCa = SelfSignedCa("CN=RediensIAM cluster CA");
        using var impostor = SelfSignedServer("cache.internal");

        Pin(ourCa)(this, impostor, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    [Fact]
    public void Name_Mismatch_Is_Refused_Even_When_The_Root_Is_Ours()
    {
        // A certificate the same cluster CA issued for a different service. The chain is perfect;
        // the identity is wrong. SslStream reports this as RemoteCertificateNameMismatch and the
        // callback must not overrule it — otherwise any workload that can get a cert from the
        // cluster issuer can impersonate the cache.
        using var ca = SelfSignedCa("CN=RediensIAM cluster CA");
        using var otherService = LeafSignedBy(ca, "some-other-service.internal");

        Pin(ca)(this, otherService, null,
            SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)
            .Should().BeFalse();
    }

    [Fact]
    public void Missing_Certificate_Is_Refused()
    {
        using var ca = SelfSignedCa("CN=RediensIAM cluster CA");

        Pin(ca)(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
    }

    [Fact]
    public void Expired_Leaf_Is_Refused()
    {
        using var ca = SelfSignedCa("CN=RediensIAM cluster CA");
        using var expired = LeafSignedBy(ca, "cache.internal",
            notBefore: DateTimeOffset.UtcNow.AddHours(-2), notAfter: DateTimeOffset.UtcNow.AddHours(-1));

        Pin(ca)(this, expired, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    [Fact]
    public void Client_Authentication_Certificate_Cannot_Open_A_Server_Connection()
    {
        using var ca = SelfSignedCa("CN=RediensIAM cluster CA");
        using var clientOnly = LeafSignedBy(ca, "cache.internal", eku: "1.3.6.1.5.5.7.3.2");

        Pin(ca)(this, clientOnly, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    [Fact]
    public void A_Certificate_The_Os_Store_Trusts_Is_Still_Refused_If_It_Is_Not_Ours()
    {
        // This is the difference between pinning and "TLS with extra steps".
        // StackExchange.Redis's own ConfigurationOptions.TrustIssuer returns true whenever
        // SslPolicyErrors is None — i.e. it also honours the public WebPKI, so the mounted root
        // stops being a requirement the moment the cache moves to a publicly-certifiable hostname.
        // This callback does not take that shortcut.
        using var ourCa = SelfSignedCa("CN=RediensIAM cluster CA");
        using var somebodyElse = SelfSignedServer("cache.internal");

        Pin(ourCa)(this, somebodyElse, null, SslPolicyErrors.None).Should().BeFalse();
    }

    // ── Wiring: when the callback is attached at all ──────────────────────────

    [Fact]
    public void Plaintext_Connection_String_Gets_No_Callback_And_No_File_Read()
    {
        // Today's deployment. Cache TLS is off, so this whole file must be inert — including for a
        // CA path that does not exist.
        var log = new List<string>();
        var options = CacheTls.BuildOptions("cache:6379,abortConnect=false", "/does/not/exist.crt", log.Add);

        options.Ssl.Should().BeFalse();
        log.Should().BeEmpty();
    }

    [Fact]
    public void Tls_Without_A_Mounted_Ca_Warns_And_Leaves_Default_Validation_In_Place()
    {
        // Not a downgrade: .NET's default validation stays, it simply cannot trust a cluster root,
        // so the connection fails loudly at startup exactly as it does today. Silently accepting
        // the certificate here would be the bug.
        var log = new List<string>();
        var options = CacheTls.BuildOptions("cache:6379,ssl=true", "/does/not/exist.crt", log.Add);

        options.Ssl.Should().BeTrue();
        log.Should().ContainSingle().Which.Should().StartWith("WARNING:").And.Contain("/does/not/exist.crt");
    }

    [Fact]
    public void Tls_With_A_Mounted_Ca_Attaches_The_Pinned_Callback()
    {
        using var ca = SelfSignedCa("CN=RediensIAM cluster CA");
        var path = WritePem(ca);
        try
        {
            var log = new List<string>();
            var options = CacheTls.BuildOptions("cache:6379,ssl=true", path, log.Add);

            options.Ssl.Should().BeTrue();
            log.Should().ContainSingle().Which.Should().Contain("pinned to 1 root(s)");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_Empty_Ca_Bundle_Refuses_To_Start()
    {
        // An empty file would otherwise produce a callback that rejects every certificate — a
        // total cache outage whose cause is one blank ConfigMap key. Fail at startup with the
        // path in the message instead.
        var path = Path.Combine(Path.GetTempPath(), $"empty-ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, "");
        try
        {
            var act = () => CacheTls.BuildOptions("cache:6379,ssl=true", path);
            act.Should().Throw<InvalidOperationException>().WithMessage("*contains no certificate*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static RemoteCertificateValidationCallback Pin(X509Certificate2 root) =>
        CacheTls.PinnedTo([root]);

    private static string WritePem(X509Certificate2 certificate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ca-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return path;
    }

    private static X509Certificate2 SelfSignedCa(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 SelfSignedServer(string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = ServerRequest(key, dnsName, "1.3.6.1.5.5.7.3.1");
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 LeafSignedBy(
        X509Certificate2 issuer,
        string dnsName,
        string eku = "1.3.6.1.5.5.7.3.1",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var key = RSA.Create(2048);
        var request = ServerRequest(key, dnsName, eku);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return request.Create(
            issuer,
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(10),
            RandomNumberGenerator.GetBytes(16));
    }

    private static CertificateRequest ServerRequest(RSA key, string dnsName, string eku)
    {
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }
}
