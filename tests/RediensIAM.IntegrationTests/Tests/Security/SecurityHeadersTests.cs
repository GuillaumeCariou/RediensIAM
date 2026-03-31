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
    public async Task AdminRoute_HasSecurityHeaders_ButNoCsp()
    {
        var res = await fixture.Client.GetAsync("/admin/config");

        res.Headers.TryGetValues("X-Content-Type-Options", out var xct).Should().BeTrue();
        xct!.First().Should().Be("nosniff");

        // Admin SPA has its own inline scripts — no CSP injected by middleware
        res.Headers.TryGetValues("Content-Security-Policy", out _).Should().BeFalse();
    }

    // ── /preview should not have X-Frame-Options: DENY ────────────────────────

    [Fact]
    public async Task PreviewRoute_NoXFrameOptions()
    {
        var res = await fixture.Client.GetAsync("/preview");

        // /preview may 404 in tests (no static file), but the header must not be set
        res.Headers.TryGetValues("X-Frame-Options", out _).Should().BeFalse();
    }
}
