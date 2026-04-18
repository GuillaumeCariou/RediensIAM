using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RediensIAM.Config;
using RediensIAM.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Unit tests for SocialLoginService — no containers required.
/// All HTTP calls are intercepted by a redirecting handler that points them at a
/// local WireMockServer, so the tests run fully in-process.
/// </summary>
public sealed class SocialLoginServiceTests : IDisposable
{
    private readonly WireMockServer          _server;
    private readonly SocialLoginService      _svc;
    private readonly IDistributedCache       _cache;

    public SocialLoginServiceTests()
    {
        _server = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        _cache  = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        _svc    = BuildSvc(_server);
    }

    private static SocialLoginService BuildSvc(WireMockServer server)
    {
        var config = new AppConfig(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:PublicUrl"]             = "https://sp.example.com",
                ["App:Domain"]                = "sp.example.com",
                ["Social:GithubUserApiUrl"]   = $"{server.Url}/user",
                ["Social:GithubEmailsApiUrl"] = $"{server.Url}/user/emails",
            })
            .Build());

        var factory = new WireMockHttpClientFactory(server.Url!);
        var cache   = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var env     = new StubWebHostEnvironment("Testing");
        return new SocialLoginService(factory, cache, config, env, NullLogger<SocialLoginService>.Instance);
    }

    public void Dispose() => _server.Dispose();

    // ── State management ──────────────────────────────────────────────────────

    [Fact]
    public async Task StoreState_ThenConsume_ReturnsOriginalData()
    {
        var data = new OAuthStateData("challenge-abc", "proj-1", "provider-github");
        var state = await _svc.StoreStateAsync(data);

        state.Should().NotBeNullOrEmpty();
        var result = await _svc.ConsumeStateAsync(state);
        result.Should().NotBeNull();
        result!.LoginChallenge.Should().Be("challenge-abc");
        result.ProjectId.Should().Be("proj-1");
        result.ProviderId.Should().Be("provider-github");
    }

    [Fact]
    public async Task ConsumeState_ConsumesOnce_SecondCallReturnsNull()
    {
        var state = await _svc.StoreStateAsync(new OAuthStateData("ch", "p", "g"));

        await _svc.ConsumeStateAsync(state);                    // first consume
        var second = await _svc.ConsumeStateAsync(state);       // already removed
        second.Should().BeNull();
    }

    [Fact]
    public async Task ConsumeState_UnknownKey_ReturnsNull()
    {
        var result = await _svc.ConsumeStateAsync("nonexistent-state-key");
        result.Should().BeNull();
    }

    // ── BuildAuthorizationUrlAsync ────────────────────────────────────────────

    [Fact]
    public async Task BuildAuthorizationUrl_GithubProvider_ContainsExpectedParams()
    {
        var provider = new ProviderConfig("gh1", "github", "client_id_x", "secret_x", null);

        var (url, state) = await _svc.BuildAuthorizationUrlAsync(provider, "login-ch", "proj-1");

        url.Should().Contain("github.com");
        url.Should().Contain("client_id=client_id_x");
        url.Should().Contain("state=");
        state.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BuildAuthorizationUrl_OidcProvider_DiscovesEndpoint()
    {
        _server
            .Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                authorization_endpoint = $"{_server.Url}/authorize",
                token_endpoint         = $"{_server.Url}/token",
                userinfo_endpoint      = $"{_server.Url}/userinfo",
            }));

        var provider = new ProviderConfig("oidc1", "oidc", "client_id_y", "secret_y", _server.Url);

        var (url, _) = await _svc.BuildAuthorizationUrlAsync(provider, "ch", "p");

        url.Should().Contain("/authorize");
    }

    [Fact]
    public async Task BuildLinkAuthorizationUrl_ReturnsUrlAndState()
    {
        var provider  = new ProviderConfig("gh1", "github", "cid", "sec", null);
        var stateData = new OAuthStateData("ch", "p", "gh1", LinkMode: true, LinkUserId: "uid");

        var (url, state) = await _svc.BuildLinkAuthorizationUrlAsync(provider, stateData);

        url.Should().Contain("github.com");
        state.Should().NotBeNullOrEmpty();
    }

    // ── GitHub profile (token exchange + user API) ────────────────────────────

    [Fact]
    public async Task ExchangeAndGetProfile_Github_ReturnsProfile()
    {
        _server
            .Given(Request.Create().WithPath("/login/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "github-tok" }));

        _server
            .Given(Request.Create().WithPath("/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = 99, login = "alice", email = "alice@github.com", name = "Alice" }));

        var provider = new ProviderConfig("gh", "github", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "auth-code");

        profile.Should().NotBeNull();
        profile!.ProviderUserId.Should().Be("99");
        profile.Email.Should().Be("alice@github.com");
        profile.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task ExchangeAndGetProfile_Github_PrivateEmail_FetchesSeparately()
    {
        _server
            .Given(Request.Create().WithPath("/login/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "tok" }));

        // /user returns no email
        _server
            .Given(Request.Create().WithPath("/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = 42, login = "bob" }));

        // /user/emails returns primary+verified email
        _server
            .Given(Request.Create().WithPath("/user/emails").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new[]
                {
                    new { email = "secondary@example.com", primary = false, verified = true },
                    new { email = "primary@example.com",   primary = true,  verified = true },
                }));

        var provider = new ProviderConfig("gh", "github", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile!.Email.Should().Be("primary@example.com");
    }

    [Fact]
    public async Task ExchangeAndGetProfile_Github_UserApiReturnsError_ReturnsNull()
    {
        _server
            .Given(Request.Create().WithPath("/login/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "tok" }));

        _server
            .Given(Request.Create().WithPath("/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var provider = new ProviderConfig("gh", "github", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().BeNull();
    }

    // ── Token exchange failure ────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeAndGetProfile_TokenExchangeFails_ReturnsNull()
    {
        _server
            .Given(Request.Create().WithPath("/login/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400)
                .WithBodyAsJson(new { error = "bad_verification_code" }));

        var provider = new ProviderConfig("gh", "github", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "bad-code");

        profile.Should().BeNull();
    }

    [Fact]
    public async Task ExchangeAndGetProfile_TokenResponseMissingAccessToken_ReturnsNull()
    {
        _server
            .Given(Request.Create().WithPath("/login/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { error = "bad_code" })); // no access_token

        var provider = new ProviderConfig("gh", "github", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().BeNull();
    }

    // ── Facebook profile ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExchangeAndGetProfile_Facebook_ReturnsProfile()
    {
        // Facebook token endpoint path: /v18.0/oauth/access_token
        _server
            .Given(Request.Create().WithPath("/v18.0/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "fb-tok" }));

        // Facebook userinfo path: /v18.0/me
        _server
            .Given(Request.Create().WithPath("/v18.0/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { id = "fb123", email = "fb@example.com", name = "FB User" }));

        var provider = new ProviderConfig("fb", "facebook", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().NotBeNull();
        profile!.ProviderUserId.Should().Be("fb123");
        profile.Email.Should().Be("fb@example.com");
    }

    [Fact]
    public async Task ExchangeAndGetProfile_Facebook_UserInfoFails_ReturnsNull()
    {
        _server
            .Given(Request.Create().WithPath("/v18.0/oauth/access_token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "tok" }));

        _server
            .Given(Request.Create().WithPath("/v18.0/me").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403));

        var provider = new ProviderConfig("fb", "facebook", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().BeNull();
    }

    // ── Standard profile (Google, GitLab, etc.) ───────────────────────────────

    [Fact]
    public async Task ExchangeAndGetProfile_Google_ReturnsProfile()
    {
        // Google token endpoint: /token
        _server
            .Given(Request.Create().WithPath("/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "goog-tok" }));

        // Google userinfo: /oauth2/v3/userinfo
        _server
            .Given(Request.Create().WithPath("/oauth2/v3/userinfo").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { sub = "goog123", email = "goog@example.com", name = "Google User" }));

        var provider = new ProviderConfig("goog", "google", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().NotBeNull();
        profile!.ProviderUserId.Should().Be("goog123");
        profile.Email.Should().Be("goog@example.com");
    }

    [Fact]
    public async Task ExchangeAndGetProfile_Standard_UserInfoMissingSubId_ReturnsNull()
    {
        _server
            .Given(Request.Create().WithPath("/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "tok" }));

        // No "sub" field → SocialUserProfile won't be created
        _server
            .Given(Request.Create().WithPath("/oauth2/v3/userinfo").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { email = "user@example.com" }));

        var provider = new ProviderConfig("goog", "google", "cid", "sec", null);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().BeNull();
    }

    // ── OIDC profile (discovery + token + userinfo) ───────────────────────────

    [Fact]
    public async Task ExchangeAndGetProfile_Oidc_ReturnsProfile()
    {
        _server
            .Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    authorization_endpoint = $"{_server.Url}/authorize",
                    token_endpoint         = $"{_server.Url}/oidc/token",
                    userinfo_endpoint      = $"{_server.Url}/oidc/userinfo",
                }));

        _server
            .Given(Request.Create().WithPath("/oidc/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "oidc-tok" }));

        _server
            .Given(Request.Create().WithPath("/oidc/userinfo").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { sub = "oidc-sub-1", email = "oidc@example.com", name = "OIDC User" }));

        var provider = new ProviderConfig("oidc1", "oidc", "cid", "sec", _server.Url);
        var profile  = await _svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().NotBeNull();
        profile!.ProviderUserId.Should().Be("oidc-sub-1");
        profile.Email.Should().Be("oidc@example.com");
        profile.Name.Should().Be("OIDC User");
    }

    [Fact]
    public async Task ExchangeAndGetProfile_Oidc_UsesCachedDiscovery()
    {
        _server
            .Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    authorization_endpoint = $"{_server.Url}/authorize",
                    token_endpoint         = $"{_server.Url}/oidc2/token",
                    userinfo_endpoint      = $"{_server.Url}/oidc2/userinfo",
                }));

        _server
            .Given(Request.Create().WithPath("/oidc2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "tok" }));

        _server
            .Given(Request.Create().WithPath("/oidc2/userinfo").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { sub = "s", email = "e@e.com" }));

        var provider = new ProviderConfig("oidc2", "oidc", "cid", "sec", _server.Url);

        // Call twice — discovery should be cached after the first call
        await _svc.ExchangeAndGetProfileAsync(provider, "code1");
        await _svc.ExchangeAndGetProfileAsync(provider, "code2");

        // Discovery endpoint should have been called only once (cached in-process)
        var discoveryHits = _server.LogEntries
            .Count(e => e.RequestMessage?.Path == "/.well-known/openid-configuration");
        discoveryHits.Should().Be(1);
    }

    [Fact]
    public async Task ExchangeAndGetProfile_ExceptionThrown_ReturnsNull()
    {
        // No stubs — all requests will fail with connection refused or 404
        var provider = new ProviderConfig("gh", "github", "cid", "sec", null);

        // WireMock returns 404 for unstubbed paths, so token exchange gets 404 → ExchangeAndGetProfileAsync catches → null
        var profile = await _svc.ExchangeAndGetProfileAsync(provider, "code");
        profile.Should().BeNull();
    }

    // ── Custom/generic OIDC provider via GetStandardProfileAsync ────────────────

    /// <summary>
    /// A provider type not in BuiltinEndpoints (e.g. "keycloak") and not "oidc"/"github"/"facebook"
    /// falls through to GetStandardProfileAsync → GetUserInfoEndpointAsync → GetDiscoveryAsync.
    /// This covers SocialLoginService lines 318-319.
    /// </summary>
    [Fact]
    public async Task ExchangeAndGetProfile_CustomOidcProvider_UsesDiscoveryForUserInfo()
    {
        _server
            .Given(Request.Create().WithPath("/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new
                {
                    authorization_endpoint = $"{_server.Url}/kc/authorize",
                    token_endpoint         = $"{_server.Url}/kc/token",
                    userinfo_endpoint      = $"{_server.Url}/kc/userinfo",
                }));

        _server
            .Given(Request.Create().WithPath("/kc/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { access_token = "kc-tok" }));

        _server
            .Given(Request.Create().WithPath("/kc/userinfo").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBodyAsJson(new { sub = "kc-sub-1", email = "kc@example.com", name = "Keycloak User" }));

        // "keycloak" is not in BuiltinEndpoints and not a special-cased type →
        // GetUserProfileAsync hits _ => GetStandardProfileAsync → GetUserInfoEndpointAsync (L318-319)
        var provider = new ProviderConfig("kc1", "keycloak", "cid", "sec", _server.Url);
        var svc      = BuildSvc(_server);   // fresh instance — no shared discovery cache
        var profile  = await svc.ExchangeAndGetProfileAsync(provider, "code");

        profile.Should().NotBeNull();
        profile!.ProviderUserId.Should().Be("kc-sub-1");
        profile.Email.Should().Be("kc@example.com");
        profile.Name.Should().Be("Keycloak User");
    }
}

// ── Infrastructure: redirecting HttpClientFactory ─────────────────────────────

/// <summary>
/// IHttpClientFactory implementation that always returns an HttpClient whose
/// message handler rewrites every request URL to the WireMock server, keeping
/// the original path and query string.  This lets us test code that uses
/// hard-coded HTTPS URLs (GitHub, Facebook, etc.) without a real network call.
/// </summary>
file sealed class WireMockHttpClientFactory(string wireMockBaseUrl) : IHttpClientFactory
{
    public HttpClient CreateClient(string name = "") =>
        new(new WireMockRedirectingHandler(wireMockBaseUrl), disposeHandler: true);
}

file sealed class WireMockRedirectingHandler(string wireMockBaseUrl) : HttpMessageHandler
{
    private readonly Uri        _base  = new(wireMockBaseUrl);
    private readonly HttpClient _inner = new(new HttpClientHandler());

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var original  = request.RequestUri!;
        var rewritten = new Uri(_base, original.PathAndQuery);

        var clone = new HttpRequestMessage(request.Method, rewritten);
        if (request.Content != null)
            clone.Content = request.Content;

        foreach (var (key, values) in request.Headers)
            clone.Headers.TryAddWithoutValidation(key, values);

        return await _inner.SendAsync(clone, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

file sealed class StubWebHostEnvironment(string environmentName) : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Test";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
