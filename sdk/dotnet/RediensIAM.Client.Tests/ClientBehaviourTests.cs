using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace RediensIAM.Client.Tests;

/// <summary>
/// The rest of the resource-server surface: the cache, the option checks, and the authentication
/// handler. <see cref="ProjectBindingTests"/> covers the project id contract itself.
///
/// The cache tests matter most. Its key is a hash of base URL, project id and token — not the token
/// alone — because <c>IMemoryCache</c> is resolved from the host, so a multi-tenant gateway shares
/// one instance across its per-tenant clients. Keyed on the token alone, tenant A's
/// <c>active: true</c> was served to tenant B, roles and all, without a round trip.
/// </summary>
public class ClientBehaviourTests
{
    private static RediensIamOptions Options(Action<RediensIamOptions>? tweak = null)
    {
        var o = new RediensIamOptions
        {
            BaseUrl             = "https://auth.example.com",
            ServiceAccountToken = "rediens_pat_x",
            ProjectId           = "proj-1",
        };
        tweak?.Invoke(o);
        return o;
    }

    /// <summary>Answers each request with the next scripted body, and counts the calls.</summary>
    private sealed class ScriptedHandler(params string[] bodies) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = bodies[Math.Min(Calls, bodies.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (RediensIamClient Client, ScriptedHandler Handler, IMemoryCache Cache) Build(
        RediensIamOptions? options = null, IMemoryCache? cache = null, params string[] bodies)
    {
        var handler = new ScriptedHandler(bodies.Length == 0 ? ["""{"active":true,"ver":2}"""] : bodies);
        var http    = new HttpClient(handler) { BaseAddress = new Uri("https://auth.example.com/") };
        var memory  = cache ?? new MemoryCache(new MemoryCacheOptions());
        return (new RediensIamClient(http, options ?? Options(), memory, NullLogger<RediensIamClient>.Instance),
                handler, memory);
    }

    // ── Options ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "rediens_pat_x", "proj-1", "BaseUrl")]
    [InlineData("   ", "rediens_pat_x", "proj-1", "BaseUrl")]
    [InlineData("https://auth.example.com", "", "proj-1", "ServiceAccountToken")]
    [InlineData("https://auth.example.com", "rediens_pat_x", "", "ProjectId")]
    [InlineData("not-a-url", "rediens_pat_x", "proj-1", "absolute URL")]
    [InlineData("http://auth.example.com", "rediens_pat_x", "proj-1", "must be https")]
    public void An_unusable_configuration_is_refused_at_construction(
        string baseUrl, string token, string projectId, string expected)
    {
        // At construction rather than on the first request: this is a deployment mistake, and it
        // should stop the process at startup rather than turn into 400s once traffic arrives.
        var options = new RediensIamOptions
        {
            BaseUrl = baseUrl, ServiceAccountToken = token, ProjectId = projectId,
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validated());
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Cleartext_is_accepted_on_loopback_and_nowhere_else()
    {
        // A check that has to be disabled for local development gets disabled in production too.
        Assert.Equal("http://localhost:4444", Options(o => o.BaseUrl = "http://localhost:4444").Validated().BaseUrl);
        Assert.Equal("http://127.0.0.1:4444", Options(o => o.BaseUrl = "http://127.0.0.1:4444").Validated().BaseUrl);
        Assert.Throws<ArgumentException>(() => Options(o => o.BaseUrl = "http://iam.internal").Validated());
    }

    [Fact]
    public async Task A_client_whose_HttpClient_is_already_in_use_keeps_the_callers_timeout()
    {
        // The timeout cannot be changed once a request has been sent, and throwing here would take
        // down a service over a setting the caller already owns.
        var http = new HttpClient(new ScriptedHandler("""{"active":true,"ver":2}"""))
        {
            BaseAddress = new Uri("https://auth.example.com/"),
        };
        using var _unused = await http.GetAsync("api/introspect");
        var before = http.Timeout;

        var ex = Record.Exception(() => new RediensIamClient(
            http, Options(o => o.Timeout = TimeSpan.FromSeconds(7)), new MemoryCache(new MemoryCacheOptions())));

        Assert.Null(ex);
        Assert.Equal(before, http.Timeout);
    }

    // ── The token info ────────────────────────────────────────────────────────

    [Fact]
    public void The_shared_inactive_answer_carries_no_roles_and_no_subject()
    {
        Assert.False(TokenInfo.Inactive.Active);
        Assert.Empty(TokenInfo.Inactive.Roles);
        Assert.Null(TokenInfo.Inactive.UserId);
        Assert.False(TokenInfo.Inactive.HasRole("super_admin"));
    }

    [Fact]
    public async Task A_tenant_role_matches_only_inside_its_own_project()
    {
        // Role names are chosen per tenant, so "admin" on its own means nothing across them.
        var (client, _, _) = Build(bodies: ["""{"active":true,"ver":2,"roles":["p1/admin","org_admin"]}"""]);

        var info = await client.IntrospectAsync("t");

        Assert.True(info.HasProjectRole("p1", "admin"));
        Assert.False(info.HasProjectRole("p2", "admin"));
        Assert.False(info.HasProjectRole("p1", "editor"));
        Assert.True(info.HasRole("org_admin"));
        Assert.False(info.HasRole("admin"));
    }

    // ── The cache ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_positive_answer_is_reused_rather_than_asked_for_twice()
    {
        var (client, handler, _) = Build();

        await client.IntrospectAsync("t");
        var second = await client.IntrospectAsync("t");

        Assert.True(second.Active);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task An_inactive_answer_is_never_cached()
    {
        // Caching it would keep denying a token that has since become valid, and buys nothing.
        var (client, handler, _) = Build(bodies: ["""{"active":false,"ver":2}"""]);

        await client.IntrospectAsync("t");
        await client.IntrospectAsync("t");

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Caching_can_be_turned_off_entirely()
    {
        var (client, handler, _) = Build(Options(o => o.CacheDuration = TimeSpan.Zero));

        await client.IntrospectAsync("t");
        await client.IntrospectAsync("t");

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task One_token_introspected_for_two_projects_is_two_questions()
    {
        // The cache is shared across a multi-tenant gateway's per-tenant clients. Keyed on the
        // token alone, tenant A's answer — roles and all — was served to tenant B.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var (a, handlerA, _) = Build(Options(o => o.ProjectId = "proj-1"), cache,
            """{"active":true,"ver":2,"roles":["p1/admin"]}""");
        var (b, handlerB, _) = Build(Options(o => o.ProjectId = "proj-2"), cache,
            """{"active":false,"ver":2}""");

        var first  = await a.IntrospectAsync("t");
        var second = await b.IntrospectAsync("t");

        Assert.True(first.Active);
        Assert.False(second.Active);
        Assert.Equal(1, handlerA.Calls);
        Assert.Equal(1, handlerB.Calls);
    }

    [Fact]
    public async Task Forgetting_a_token_makes_the_next_question_reach_the_server()
    {
        var (client, handler, _) = Build();
        await client.IntrospectAsync("t");

        client.Forget("t");
        await client.IntrospectAsync("t");

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task An_empty_token_is_inactive_without_a_round_trip()
    {
        var (client, handler, _) = Build();

        Assert.False((await client.IntrospectAsync("")).Active);
        Assert.False((await client.IntrospectAsync("   ")).Active);
        Assert.Equal(0, handler.Calls);
    }

    // ── Faults ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_server_fault_throws_rather_than_denying_everyone_quietly()
    {
        // Treating an outage as "token invalid" degrades to denying every request, silently.
        var (client, handler, _) = Build();
        handler.Status = HttpStatusCode.InternalServerError;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.IntrospectAsync("t"));
    }

    [Fact]
    public async Task An_empty_body_is_a_broken_server_rather_than_an_inactive_token()
    {
        var (client, _, _) = Build(bodies: ["null"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.IntrospectAsync("t"));
        Assert.Contains("empty introspection answer", ex.Message);
    }

    [Fact]
    public async Task An_empty_authorisation_body_is_reported_the_same_way()
    {
        var (client, _, _) = Build(bodies: ["null"]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AuthorizeAsync("t", "Organisations", "org-1", "org_admin"));
        Assert.Contains("empty authorisation answer", ex.Message);
    }

    [Fact]
    public async Task An_authorisation_the_server_refuses_comes_back_false_rather_than_throwing()
    {
        var (client, _, _) = Build(bodies: ["""{"allowed":false,"user_id":"u1","ver":2}"""]);

        Assert.False(await client.AuthorizeAsync("t", "Organisations", "org-1", "org_admin"));
    }

    [Fact]
    public async Task The_debug_line_is_written_only_when_debug_logging_is_on()
    {
        // Guarded because introspection runs per request, and the unguarded call boxes the
        // arguments into the params array even when Debug is off.
        var recorder = new RecordingLogger { Enabled = false };
        var http = new HttpClient(new ScriptedHandler("""{"active":true,"ver":2,"user_id":"u1"}"""))
        {
            BaseAddress = new Uri("https://auth.example.com/"),
        };
        var client = new RediensIamClient(http, Options(), new MemoryCache(new MemoryCacheOptions()), recorder);

        await client.IntrospectAsync("t");
        Assert.Empty(recorder.Messages);

        recorder.Enabled = true;
        await client.IntrospectAsync("another-token");
        Assert.Single(recorder.Messages);
        Assert.Contains("active=True", recorder.Messages[0]);
    }

    private sealed class RecordingLogger : ILogger<RediensIamClient>
    {
        public bool Enabled { get; set; }
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => Enabled;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_server_fault_on_authorisation_throws_too()
    {
        var (client, handler, _) = Build();
        handler.Status = HttpStatusCode.BadGateway;

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.AuthorizeAsync("t", "Organisations", "org-1", "org_admin"));
    }

    // ── The authentication handler ────────────────────────────────────────────

    private static async Task<AuthenticateResult> Authenticate(string? header, params string[] bodies)
    {
        var (client, _, _) = Build(bodies: bodies);
        var handler = new RediensIamAuthenticationHandler(
            new OptionsMonitorStub(), NullLoggerFactory.Instance, UrlEncoder.Default, client);

        var context = new DefaultHttpContext();
        if (header is not null) context.Request.Headers.Authorization = header;

        await handler.InitializeAsync(
            new AuthenticationScheme(RediensIamDefaults.Scheme, null, typeof(RediensIamAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic abc")]
    public async Task A_request_with_no_bearer_is_left_for_another_scheme(string? header)
    {
        var result = await Authenticate(header);

        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task A_live_token_becomes_a_principal_carrying_its_tenant_and_roles()
    {
        var result = await Authenticate("Bearer tok",
            """{"active":true,"ver":2,"user_id":"u1","org_id":"o1","project_id":"p1","roles":["p1/admin","org_admin"]}""");

        Assert.True(result.Succeeded);
        var principal = result.Principal!;
        Assert.Equal("u1", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("o1", principal.FindFirstValue(RediensIamDefaults.OrgIdClaim));
        Assert.Equal("p1", principal.FindFirstValue(RediensIamDefaults.ProjectIdClaim));
        // The qualified name is what lands in the role claim, so [Authorize(Roles = "admin")]
        // fails closed rather than matching every tenant's "admin".
        Assert.True(principal.IsInRole("p1/admin"));
        Assert.False(principal.IsInRole("admin"));
    }

    [Fact]
    public async Task The_scheme_tolerates_a_bearer_written_in_any_case_and_padded()
    {
        var result = await Authenticate("bearer   tok   ", """{"active":true,"ver":2,"user_id":"u1"}""");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_token_the_server_calls_inactive_is_refused()
    {
        var result = await Authenticate("Bearer tok", """{"active":false,"ver":2}""");

        Assert.False(result.Succeeded);
        Assert.Equal("Token is not active.", result.Failure!.Message);
    }

    [Fact]
    public async Task An_answer_with_no_identity_in_it_yields_a_principal_with_no_claims()
    {
        var result = await Authenticate("Bearer tok", """{"active":true,"ver":2,"user_id":"","org_id":null}""");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Principal!.Claims);
    }

    [Fact]
    public async Task An_IAM_outage_fails_the_request_rather_than_letting_it_through()
    {
        // The one thing this must never do is degrade to unauthenticated: that is an
        // authorisation bypass, not a graceful degradation.
        var (client, handler, _) = Build();
        handler.Status = HttpStatusCode.ServiceUnavailable;
        var authHandler = new RediensIamAuthenticationHandler(
            new OptionsMonitorStub(), NullLoggerFactory.Instance, UrlEncoder.Default, client);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer tok";
        await authHandler.InitializeAsync(
            new AuthenticationScheme(RediensIamDefaults.Scheme, null, typeof(RediensIamAuthenticationHandler)),
            context);

        var result = await authHandler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.IsType<HttpRequestException>(result.Failure);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public void Registration_refuses_a_configuration_the_client_would_refuse()
    {
        // At registration, so the failure lands at startup with the call in the stack trace.
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddRediensIam(o =>
        {
            o.BaseUrl             = "https://auth.example.com";
            o.ServiceAccountToken = "rediens_pat_x";
            // No project id.
        }));
    }

    [Fact]
    public void Registration_wires_up_a_usable_client()
    {
        var services = new ServiceCollection();
        services.AddRediensIam(o =>
        {
            o.BaseUrl             = "https://auth.example.com";
            o.ServiceAccountToken = "rediens_pat_x";
            o.ProjectId            = "proj-1";
            o.Timeout             = TimeSpan.FromSeconds(9);
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<RediensIamClient>());
        Assert.Equal("proj-1", provider.GetRequiredService<RediensIamOptions>().ProjectId);
        Assert.Equal(TimeSpan.FromSeconds(9),
            provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(RediensIamClient)).Timeout);
    }

    [Fact]
    public async Task The_authentication_scheme_can_be_added()
    {
        var services = new ServiceCollection();
        services.AddRediensIam(o =>
        {
            o.BaseUrl             = "https://auth.example.com";
            o.ServiceAccountToken = "rediens_pat_x";
            o.ProjectId            = "proj-1";
        });

        services.AddAuthentication(RediensIamDefaults.Scheme).AddRediensIam();

        using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync(RediensIamDefaults.Scheme));
    }
}
