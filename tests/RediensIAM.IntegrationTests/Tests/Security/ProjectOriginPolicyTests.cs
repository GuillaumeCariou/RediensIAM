using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// The deployment used to decide which origins it would talk to from two values frozen at
/// startup, <c>App__PublicUrl</c> and <c>App__AdminSpaOrigin</c>. The OIDC flow does not work
/// that way: the browser has to be able to end up on <b>any</b> redirect_uri a project has
/// registered, and those are created in the console long after the process started.
///
/// <para>
/// The symptom was one line in a browser console, and it named the mechanism exactly:
/// <c>Connecting to 'https://superadmin.yandee.fr/?error=…' violates the following Content
/// Security Policy directive: "connect-src 'self'"</c>. The login page fetches
/// <c>GET /auth/login?login_challenge=…</c>; on the skip and reject branches that endpoint
/// answers <b>302</b>, the browser follows the chain through Hydra, and the last hop lands on
/// the client's registered redirect_uri — a cross-origin hop inside a <c>fetch</c>, which is
/// precisely what <c>connect-src</c> governs.
/// </para>
///
/// <para>
/// Three server-side allowlists shared that frozen root, and all three are covered here: the CSP
/// header, the CORS policy, and <c>SafeRedirect</c>'s trusted-origin set. The origins now come
/// from the registered clients themselves, so creating a project is the only step needed.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ProjectOriginPolicyTests(TestFixture fixture)
{
    private const string ProjectOrigin = "https://superadmin.origin-test";
    private const string OtherOrigin   = "https://unregistered.origin-test";

    /// <summary>
    /// Registers one client with <paramref name="redirectUris"/>, drops whatever the app had
    /// cached, runs <paramref name="body"/>, then puts both back. The reset matters: the fixture
    /// is shared by the whole collection, and a leaked origin would silently widen every other
    /// test's idea of what this deployment trusts.
    /// </summary>
    private async Task WithRegisteredClientAsync(string[] redirectUris, Func<Task> body,
        string[]? postLogoutUris = null)
    {
        fixture.Hydra.SetupRegisteredClients(
            new StubOAuth2Client("origin-test-client", redirectUris, postLogoutUris));
        fixture.GetService<ClientOriginsService>().Invalidate();
        try
        {
            await body();
        }
        finally
        {
            fixture.Hydra.RestoreRegisteredClients();
            fixture.GetService<ClientOriginsService>().Invalidate();
        }
    }

    private async Task<string> PublicCspAsync()
    {
        var res = await fixture.Client.GetAsync("/health");
        return res.Headers.GetValues("Content-Security-Policy").Single();
    }

    // ── CSP ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported failure. A project registers <c>https://superadmin…/</c> as its redirect_uri;
    /// the login page must be allowed to reach it, so connect-src has to name it.
    /// </summary>
    [Fact]
    public async Task PublicCsp_ConnectSrc_NamesTheOriginOfARegisteredRedirectUri()
    {
        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            var policy = await PublicCspAsync();

            policy.Should().Contain(ProjectOrigin,
                "the login flow's last hop lands on the client's registered redirect_uri");
        });
    }

    /// <summary>
    /// Hydra refuses a post-logout redirect the client has not whitelisted, so those URIs are
    /// registered too — and the browser reaches them the same way.
    /// </summary>
    [Fact]
    public async Task PublicCsp_ConnectSrc_NamesTheOriginOfAPostLogoutRedirectUri()
    {
        await WithRegisteredClientAsync(
            ["https://app.origin-test/callback"],
            async () =>
            {
                var policy = await PublicCspAsync();

                policy.Should().Contain(ProjectOrigin);
            },
            postLogoutUris: [$"{ProjectOrigin}/goodbye"]);
    }

    /// <summary>
    /// Deriving the allowlist is not the same as widening it. An origin no client registered has
    /// no business in the header — that is the whole difference between this and <c>connect-src *</c>.
    /// </summary>
    [Fact]
    public async Task PublicCsp_ConnectSrc_DoesNotNameAnOriginNoClientRegistered()
    {
        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            var policy = await PublicCspAsync();

            policy.Should().NotContain(OtherOrigin);
            policy.Should().NotContain("connect-src *");
        });
    }

    /// <summary>
    /// Native and mobile clients register custom schemes — <c>myapp://callback</c>. A CSP source
    /// list is not a redirect allowlist: those are meaningless there at best, and at worst they
    /// are a token the parser will not read as the author intended.
    /// </summary>
    [Fact]
    public async Task PublicCsp_ConnectSrc_IgnoresNonHttpRedirectUriSchemes()
    {
        await WithRegisteredClientAsync(["myapp://callback", $"{ProjectOrigin}/"], async () =>
        {
            var policy = await PublicCspAsync();

            policy.Should().NotContain("myapp:");
            policy.Should().Contain(ProjectOrigin, "the http(s) sibling still belongs there");
        });
    }

    /// <summary>
    /// A redirect_uri is supplied by whoever administers the tenant, and it now reaches a response
    /// header. If the value could carry <c>;</c> it would not widen one directive — it would end
    /// it and start another, and <c>script-src 'unsafe-inline'</c> is one semicolon away.
    /// </summary>
    [Theory]
    [InlineData("https://evil.origin-test/;script-src 'unsafe-inline'")]
    [InlineData("https://evil.origin-test/ 'unsafe-inline'")]
    [InlineData("https://evil.origin-test/\r\nX-Injected: 1")]
    public async Task PublicCsp_CannotBeInjectedThroughARegisteredRedirectUri(string hostile)
    {
        await WithRegisteredClientAsync([hostile], async () =>
        {
            var res = await fixture.Client.GetAsync("/health");
            var policy = res.Headers.GetValues("Content-Security-Policy").Single();

            // Asserting on the connect-src directive itself, not on the policy as a whole: the
            // policy legitimately contains 'unsafe-inline' under style-src, so a substring test
            // over the header would pass while the injection succeeded one directive away.
            var connectSrc = policy
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Single(d => d.StartsWith("connect-src ", StringComparison.Ordinal));

            foreach (var token in connectSrc["connect-src ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                token.Should().MatchRegex(@"^('self'|https?://[A-Za-z0-9.\-_:\[\]]+)$",
                    "a redirect_uri reaches this header, so every token in it must be one this code built");
            }

            // And nothing escaped into the header block either.
            res.Headers.Contains("X-Injected").Should().BeFalse();
            policy.Should().NotContain("X-Injected");
        });
    }

    /// <summary>
    /// The console branch stays narrow. Its own redirect_uri is same-origin by construction, so
    /// every tenant origin added there would be reach the console never needs.
    /// </summary>
    [Fact]
    public async Task ConsoleCsp_ConnectSrc_IsNotWidenedByTenantOrigins()
    {
        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            var res = await fixture.Client.GetAsync("/console/config");
            var policy = res.Headers.GetValues("Content-Security-Policy").First();

            policy.Should().NotContain(ProjectOrigin);
        });
    }

    // ── CORS ──────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/console/config");
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "GET");
        return await fixture.Client.SendAsync(req);
    }

    /// <summary>
    /// Same root, second symptom: every new front had to be added by hand to the CORS policy —
    /// and to Hydra's <c>allowed_origins</c> beside it — before it could call the API at all.
    /// </summary>
    [Fact]
    public async Task Cors_AllowsAnOriginARegisteredRedirectUriNames()
    {
        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            var res = await PreflightAsync(ProjectOrigin);

            res.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowed).Should().BeTrue(
                "a front whose redirect_uri this deployment registered is a front it talks to");
            allowed!.Single().Should().Be(ProjectOrigin);
        });
    }

    /// <summary>The allowlist is still an allowlist.</summary>
    [Fact]
    public async Task Cors_StillRefusesAnOriginNoClientRegistered()
    {
        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            var res = await PreflightAsync(OtherOrigin);

            res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        });
    }

    // ── SafeRedirect ──────────────────────────────────────────────────────────

    /// <summary>
    /// The third frozen allowlist. When Hydra accepts or rejects a login it answers with the
    /// client's own redirect_uri, and <c>SafeRedirect</c> turned that into a 400 because the
    /// origin was not one of the three it knew — so the flow died on the server before the CSP
    /// ever got a say.
    /// </summary>
    [Fact]
    public async Task SkipLogin_RedirectsToARegisteredRedirectUri()
    {
        const string target = $"{ProjectOrigin}/?code=abc";
        var challenge = $"origin-skip-{Guid.NewGuid()}";

        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            fixture.Hydra.SetupLoginChallenge(challenge, "origin-test-client", skip: true, subject: "");
            fixture.Hydra.SetupLoginAcceptRedirect(target);
            try
            {
                var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

                res.StatusCode.Should().Be(HttpStatusCode.Found);
                res.Headers.Location!.ToString().Should().StartWith(ProjectOrigin);
            }
            finally
            {
                fixture.Hydra.RestoreLoginAcceptRedirect();
            }
        });
    }

    /// <summary>
    /// And it still refuses what no client registered — a poisoned upstream response must not
    /// become an open redirect just because the allowlist learned to grow.
    /// </summary>
    [Fact]
    public async Task SkipLogin_StillRefusesARedirectNoClientRegistered()
    {
        var challenge = $"origin-skip-bad-{Guid.NewGuid()}";

        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
        {
            fixture.Hydra.SetupLoginChallenge(challenge, "origin-test-client", skip: true, subject: "");
            fixture.Hydra.SetupLoginAcceptRedirect($"{OtherOrigin}/?code=abc");
            try
            {
                var res = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");

                res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            finally
            {
                fixture.Hydra.RestoreLoginAcceptRedirect();
            }
        });
    }

    // ── Freshness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The point of the whole change: creating a project in the console is enough. Nothing here
    /// restarts the process or edits a values file — the client appears in Hydra and the next
    /// request already trusts its origin.
    /// </summary>
    [Fact]
    public async Task ANewlyRegisteredClientIsTrustedWithoutRestartingAnything()
    {
        (await PublicCspAsync()).Should().NotContain(ProjectOrigin);

        await WithRegisteredClientAsync([$"{ProjectOrigin}/"], async () =>
            (await PublicCspAsync()).Should().Contain(ProjectOrigin));

        (await PublicCspAsync()).Should().NotContain(ProjectOrigin);
    }
}
