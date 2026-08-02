using System.Net.Sockets;
using RediensIAM.Controllers;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Pure-unit regression tests for the two allowlist/denylist validators.
/// Each test pins a bypass that the original implementation accepted.
/// No fixture — these must stay fast and runnable without containers.
/// </summary>
public class RedirectAndSsrfRegressionTests
{
    private static readonly string[] Trusted = ["https://iam.example.com", "https://admin.example.com"];

    // ── REG-SEC-03: open redirect via backslash ──────────────────────────────
    // RedirectValidator short-circuits on "starts with '/' and not '//'" and treats
    // the value as a same-origin path. Browsers normalise a leading "/\" to "//",
    // i.e. protocol-relative, so "/\evil.com" navigates off-origin.
    // The login SPA's safeNavigate.ts already rejects backslashes; the server did not.

    [Theory]
    [InlineData(@"/\evil.com")]
    [InlineData(@"/\/evil.com")]
    [InlineData(@"/\\evil.com")]
    [InlineData("/\\evil.com/path?x=1")]
    public void TryReconstruct_BackslashRelativePath_IsRejected(string url)
    {
        var allowed = RedirectValidator.TryReconstruct(url, Trusted, out var safeUrl);

        allowed.Should().BeFalse(
            "a leading /\\ is normalised to // (protocol-relative) by browsers and escapes the origin");
        safeUrl.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/callback")]
    [InlineData("/oauth2/error?login_challenge=abc")]
    [InlineData("https://iam.example.com/consent")]
    public void TryReconstruct_LegitimateTargets_StillAllowed(string url)
    {
        RedirectValidator.TryReconstruct(url, Trusted, out var safeUrl).Should().BeTrue();
        safeUrl.Should().NotBeEmpty();
    }

    [Fact]
    public void TryReconstruct_ProtocolRelative_IsStillRejected()
    {
        RedirectValidator.TryReconstruct("//evil.com/x", Trusted, out _).Should().BeFalse();
    }

    // ── REG-SEC-04: SSRF denylist bypasses ───────────────────────────────────
    // WebhookUrlValidator.IsPrivateIp only handled plain IPv4 private ranges plus
    // IPv6 loopback/link-local/site-local. Three families slipped through:
    //   a) IPv4-mapped IPv6 (::ffff:10.0.0.1) — the v6 branch returns early
    //   b) IPv6 unique-local fc00::/7 — IsIPv6SiteLocal only covers the deprecated fec0::/10
    //   c) CGNAT 100.64.0.0/10 — the range Tailscale uses, and this deployment
    //      exposes its admin ingress on 100.64.0.3

    [Theory]
    [InlineData("::ffff:10.0.0.1")]      // IPv4-mapped RFC1918
    [InlineData("::ffff:127.0.0.1")]     // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped cloud metadata
    [InlineData("::ffff:192.168.1.97")]  // IPv4-mapped LAN
    public void IsPrivateIp_Ipv4MappedIpv6_IsBlocked(string ip)
    {
        WebhookUrlValidator.IsPrivateIp(IPAddress.Parse(ip)).Should().BeTrue(
            "an IPv4-mapped IPv6 literal reaches the same host as the bare IPv4 address");
    }

    [Theory]
    [InlineData("fd00::1")]              // unique-local (RFC 4193)
    [InlineData("fc00::1")]
    [InlineData("fdff:ffff::1")]
    public void IsPrivateIp_UniqueLocalIpv6_IsBlocked(string ip)
    {
        WebhookUrlValidator.IsPrivateIp(IPAddress.Parse(ip)).Should().BeTrue(
            "fc00::/7 is the IPv6 equivalent of RFC1918 and is not covered by IsIPv6SiteLocal");
    }

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.64.0.3")]           // this cluster's Tailscale admin ingress
    [InlineData("100.127.255.254")]
    public void IsPrivateIp_CgnatRange_IsBlocked(string ip)
    {
        WebhookUrlValidator.IsPrivateIp(IPAddress.Parse(ip)).Should().BeTrue(
            "100.64.0.0/10 is the CGNAT / Tailscale range and routes inside the private network");
    }

    [Theory]
    [InlineData("100.63.255.255")]       // just below CGNAT
    [InlineData("100.128.0.0")]          // just above CGNAT
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700::1111")]      // public IPv6
    public void IsPrivateIp_PublicAddresses_StillAllowed(string ip)
    {
        WebhookUrlValidator.IsPrivateIp(IPAddress.Parse(ip)).Should().BeFalse();
    }

    [Fact]
    public void IsPrivateIp_AddressFamilyCoverage_IsExplicit()
    {
        // Guards against a regression where the v6 branch returns before the
        // mapped-v4 check runs.
        var mapped = IPAddress.Parse("::ffff:10.1.2.3");
        mapped.AddressFamily.Should().Be(AddressFamily.InterNetworkV6);
        mapped.IsIPv4MappedToIPv6.Should().BeTrue();
        WebhookUrlValidator.IsPrivateIp(mapped).Should().BeTrue();
    }
}
