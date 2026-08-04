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
        var res = await fixture.Client.GetAsync("/console/config");

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
        var res = await fixture.Client.GetAsync("/console/config");
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
        // connect-src used to be asserted as exactly "'self';". That assertion encoded the defect:
        // the login flow's last hop lands on a client's registered redirect_uri, which 'self' can
        // never cover, so the header has to be able to name origins this deployment did not know at
        // startup. What must stay true is that it names them one by one — see
        // ProjectOriginPolicyTests for the per-token shape.
        policy.Should().Contain("connect-src 'self'");
        policy.Should().NotContain("connect-src *");
        policy.Should().NotContain("connect-src 'self' *");
    }

    // ── The /preview framing exemption, reinstated correctly ─────────────────
    //
    // It was removed once, and the removal was right at the time: it named a route that did not
    // exist, and it left `frame-ancestors 'none'` on the same response, which browsers honour over
    // X-Frame-Options — so it exempted nothing. It is back because the console genuinely frames
    // that page: the project Authentication screen renders the login SPA's /preview route in an
    // iframe so an operator can see a project's branding before saving it. Both halves move
    // together now, and the page itself is inert — it takes its configuration from the query
    // string, posts nothing, and accepts no credentials.

    [Fact]
    public async Task PreviewRoute_IsFramableByTheAdminConsole()
    {
        var res = await fixture.Client.GetAsync("/preview");

        // X-Frame-Options has no per-origin form, so the only way to allow one origin is to omit it
        // and let CSP carry the rule. Sending DENY alongside would re-block the iframe.
        res.Headers.Contains("X-Frame-Options").Should().BeFalse();

        var policy = res.Headers.GetValues("Content-Security-Policy").Single();
        // 'self' alone: the console frames "/preview?cfg=…" relatively, so the frame is same-origin
        // by construction. A second origin here would widen the policy for a case that cannot occur.
        policy.Should().Contain("frame-ancestors 'self';");
        policy.Should().NotContain("frame-ancestors 'none'");
    }

    /// <summary>
    /// The exemption is matched with <c>StartsWithSegments</c>, which stops at a path-segment
    /// boundary. A prefix match would have handed the same exemption to any route whose name
    /// merely begins with the word.
    /// </summary>
    [Theory]
    [InlineData("/previewer")]
    [InlineData("/preview-anything")]
    [InlineData("/login")]
    [InlineData("/health")]
    // The SPA fallback serves index.html for these, and the login SPA's catch-all route renders
    // the real login form — password field and all. Framing that is the clickjacking case the
    // exemption must not reach, so the match has to be the exact path, not a prefix, and not
    // case-insensitive.
    [InlineData("/preview/x")]
    [InlineData("/preview/anything/deeper")]
    [InlineData("/PREVIEW")]
    public async Task EveryOtherRoute_IsStillDeniedFraming(string path)
    {
        var res = await fixture.Client.GetAsync(path);

        res.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        res.Headers.GetValues("Content-Security-Policy").Single()
           .Should().Contain("frame-ancestors 'none'");
    }

    /// <summary>
    /// The console reads its own version from here rather than carrying a constant: a SPA built
    /// against one release and served by another would otherwise report the build it came from.
    /// The sidebar showed a hardcoded "v0.1" for exactly that reason.
    /// </summary>
    [Fact]
    public async Task AdminConfig_ReportsTheRunningServerVersion()
    {
        var res = await fixture.Client.GetAsync("/console/config");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var version = body.GetProperty("version").GetString();
        version.Should().NotBeNullOrWhiteSpace();
        version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    /// <summary>
    /// `RequireHost("*:{AdminPort}")` matches the Host *header*, not the port the connection
    /// arrived on, and host filtering strips the port before comparing — so an internet caller on
    /// the public port could scrape the full Prometheus surface by sending the admin port in the
    /// Host header. Swagger gets this right two lines away by reading Connection.LocalPort.
    /// </summary>
    [Fact]
    public async Task Metrics_IsNotReachableOnThePublicPortByForgingTheHostHeader()
    {
        var adminPort = fixture.GetService<RediensIAM.Config.AppConfig>().AdminPort;
        var req = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        req.Headers.Host = $"localhost:{adminPort}";

        var res = await fixture.Client.SendAsync(req);

        res.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "the metrics endpoint is bound to the admin port, and a header is not a port");
    }
}
