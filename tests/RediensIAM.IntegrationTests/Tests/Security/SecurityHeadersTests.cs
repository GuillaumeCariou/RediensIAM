using System.Net.Http.Json;
using System.Text.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Security;

[Collection("RediensIAM")]
public class SecurityHeadersTests(TestFixture fixture)
{
    // ── Login SPA (public routes) ─────────────────────────────────────────────

    [Fact]
    public async Task PublicRoute_HasXContentTypeOptions()
    {
        var res = await fixture.Client.GetAsync("/auth/login?login_challenge=dummy");

        res.Headers.TryGetValues("X-Content-Type-Options", out var values).Should().BeTrue();
        values!.First().Should().Be("nosniff");
    }

    [Fact]
    public async Task PublicRoute_HasXFrameOptionsDeny()
    {
        var res = await fixture.Client.GetAsync("/health");

        res.Headers.TryGetValues("X-Frame-Options", out var values).Should().BeTrue();
        values!.First().Should().Be("DENY");
    }

    [Fact]
    public async Task PublicRoute_HasReferrerPolicy()
    {
        var res = await fixture.Client.GetAsync("/health");

        res.Headers.TryGetValues("Referrer-Policy", out var values).Should().BeTrue();
        values!.First().Should().Be("strict-origin-when-cross-origin");
    }

    [Fact]
    public async Task PublicRoute_HasPermissionsPolicy()
    {
        var res = await fixture.Client.GetAsync("/health");

        res.Headers.TryGetValues("Permissions-Policy", out var values).Should().BeTrue();
        values!.First().Should().Contain("geolocation=()");
    }

    [Fact]
    public async Task PublicRoute_HasContentSecurityPolicy()
    {
        var res = await fixture.Client.GetAsync("/health");

        res.Headers.TryGetValues("Content-Security-Policy", out var values).Should().BeTrue();
        values!.First().Should().Contain("default-src 'self'");
    }

    // ── Admin routes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminRoute_HasSecurityHeaders_WithRelaxedCsp()
    {
        var res = await fixture.Client.GetAsync("/admin/config");

        res.Headers.TryGetValues("X-Content-Type-Options", out var xct).Should().BeTrue();
        xct!.First().Should().Be("nosniff");

        res.Headers.TryGetValues("Content-Security-Policy", out var csp).Should().BeTrue();
        var policy = csp!.First();
        // Scripts stay strict — 'unsafe-inline' is granted to style-src only (Radix injects
        // <style> nodes at runtime), never to script-src.
        policy.Should().Contain("script-src 'self';");
        policy.Should().NotContain("script-src 'self' 'unsafe-inline'");
        policy.Should().Contain("style-src 'self' 'unsafe-inline'");
        policy.Should().Contain("object-src 'none'");
    }

    // ── R-26 — the admin console could not reach its own issuer ───────────────
    // connect-src fell back to default-src 'self', so oidc-client-ts's discovery fetch to
    // {issuer}/.well-known/openid-configuration was blocked and admin login was impossible.

    [Fact]
    public async Task AdminRoute_ConnectSrc_NamesTheIssuerOrigin()
    {
        var res = await fixture.Client.GetAsync("/admin/config");
        var cfg = await res.Content.ReadFromJsonAsync<JsonElement>();
        var issuerOrigin = new Uri(cfg.GetProperty("hydra_url").GetString()!)
            .GetLeftPart(UriPartial.Authority);

        var policy = res.Headers.GetValues("Content-Security-Policy").First();
        policy.Should().Contain($"connect-src 'self' {issuerOrigin};");
    }

    [Fact]
    public async Task PublicRoute_AllowsRemoteImagesButNotRemoteScripts()
    {
        var res = await fixture.Client.GetAsync("/health");

        var policy = res.Headers.GetValues("Content-Security-Policy").First();
        // Tenant logos and social-provider icons are remote HTTPS images.
        policy.Should().Contain("img-src 'self' data: https:");
        policy.Should().Contain("script-src 'self';");
        policy.Should().Contain("connect-src 'self';");
    }

    // ── I-03: the /preview framing exemption was dead in two ways ─────────────
    // No /preview route exists anywhere in src/, and the same response still carried
    // frame-ancestors 'none', which browsers honour over X-Frame-Options. The exemption is gone;
    // every response is DENY.

    [Fact]
    public async Task PreviewRoute_IsFramingDeniedLikeEverythingElse()
    {
        var res = await fixture.Client.GetAsync("/preview");

        res.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
    }
}
