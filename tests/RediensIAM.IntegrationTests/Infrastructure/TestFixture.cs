using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Fido2NetLib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using RediensIAM.Controllers;
using RediensIAM.Services;
using ISmsService = RediensIAM.Services.ISmsService;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace RediensIAM.IntegrationTests.Infrastructure;

/// <summary>
/// xUnit collection fixture: one set of containers + one WebApplicationFactory
/// shared across all integration tests in the "RediensIAM" collection.
/// </summary>
[CollectionDefinition("RediensIAM")]
public class TestCollection : ICollectionFixture<TestFixture> { }

public sealed class TestFixture : IAsyncLifetime
{
    // ── Containers ────────────────────────────────────────────────────────────
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("rediensiam_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder("docker.dragonflydb.io/dragonflydb/dragonfly:latest").Build();

    private readonly IContainer _mailhog = new ContainerBuilder("mailhog/mailhog:v1.0.1")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025)))
        .Build();

    // ── Stubs ─────────────────────────────────────────────────────────────────
    public HydraStub        Hydra      { get; } = new();
    public KetoStub         Keto       { get; } = new();
    public StubEmailService EmailStub  { get; } = new();
    public StubSmsService   SmsStub    { get; } = new();
    public HibpStubHandler  HibpStub   { get; } = new();

    // ── Cache connection (for flush between tests) ────────────────────────────
    private ConnectionMultiplexer? _mux;

    // ── App under test ────────────────────────────────────────────────────────
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Direct DB access for seeding and assertions.</summary>
    public RediensIamDbContext Db { get; private set; } = null!;

    /// <summary>Root DI service provider from the test host.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>Helper for creating test data.</summary>
    public SeedData Seed { get; private set; } = null!;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Start containers in parallel (MailHog for real SMTP coverage tests)
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _mailhog.StartAsync());

        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Database
                        ["ConnectionStrings:Default"]           = _postgres.GetConnectionString(),

                        // Redis
                        ["Cache:ConnectionString"]              = _redis.GetConnectionString(),
                        ["Cache:InstanceName"]                  = "test:",
                        ["Cache:PatTtlMinutes"]                 = "5",

                        // App
                        ["App:PublicUrl"]                       = "http://localhost",
                        ["App:Domain"]                          = "localhost",
                        ["App:AdminSpaOrigin"]                  = "http://localhost",
                        ["IAM_PUBLIC_PORT"]                     = "5000",
                        ["IAM_ADMIN_PORT"]                      = "5001",

                        // Security — 32-byte all-zero AES key (test only)
                        ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                        ["Security:MaxLoginAttempts"]           = "5",
                        ["Security:LockoutMinutes"]             = "15",
                        ["Security:OtpTtlSeconds"]              = "300",
                        ["Security:MaxSmsPerWindow"]            = "3",
                        ["Security:SmsWindowMinutes"]           = "10",
                        ["Security:PatPrefix"]                  = "rediens_pat_",
                        ["Security:ArgonTimeCost"]              = "1",  // fast in tests
                        ["Security:ArgonMemoryCost"]            = "8192",
                        ["Security:ArgonParallelism"]           = "1",

                        // Ory stubs
                        ["Hydra:AdminUrl"]                      = Hydra.Url,
                        ["Hydra:PublicUrl"]                     = Hydra.Url,
                        ["Keto:ReadUrl"]                        = Keto.ReadUrl,
                        ["Keto:WriteUrl"]                       = Keto.WriteUrl,

                        // SMTP — disabled in tests (stub email service registered below)
                        ["Smtp:Host"]                           = "",
                        ["Smtp:FromAddress"]                    = "noreply@test.com",
                        ["Smtp:FromName"]                       = "Test IAM",

                        // No bootstrap user
                        ["Bootstrap:Email"]                     = "",
                        ["Bootstrap:Password"]                  = "",
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // TestServer doesn't set RemoteIpAddress — inject loopback so IP allowlist tests work
                    services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    // Replace real email service with the shared singleton stub
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddSingleton<IEmailService>(EmailStub);

                    // Replace SMS service with capturing stub
                    var smsDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISmsService));
                    if (smsDescriptor != null) services.Remove(smsDescriptor);
                    services.AddSingleton<ISmsService>(SmsStub);

                    // Intercept unnamed HTTP client (used by BreachCheckService / SocialLoginService)
                    // with a stub that can be configured per-test to return HIBP breach counts.
                    var hibp = HibpStub;
                    services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => hibp);

                    // Allow session cookies over plain HTTP in tests (test server uses http://localhost)
                    services.Configure<SessionOptions>(opts =>
                    {
                        opts.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        opts.Cookie.SameSite    = SameSiteMode.Unspecified;
                    });

                    DisableCircuitBreakers(services);
                    // Allow localhost/loopback webhook delivery so tests can use WireMock as a target
                    var ssrfDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IWebhookSsrfValidator));
                    if (ssrfDesc != null) services.Remove(ssrfDesc);
                    services.AddSingleton<IWebhookSsrfValidator, PassthroughSsrfValidator>();
                });
            });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // Get a direct DB context for seeding
        var scope = _factory.Services.CreateScope();
        Db   = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
        var pwd = scope.ServiceProvider.GetRequiredService<PasswordService>();
        Seed = new SeedData(Db, Hydra, pwd);
    }

    public async Task DisposeAsync()
    {
        Hydra.Dispose();
        Keto.Dispose();
        _mux?.Dispose();
        Client.Dispose();
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _mailhog.DisposeAsync().AsTask());
    }

    // ── Request helpers ───────────────────────────────────────────────────────

    /// <summary>Returns an HttpClient pre-loaded with a bearer token for the given user.</summary>
    public HttpClient ClientWithToken(string token)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Creates a fresh HttpClient with its own cookie jar — use in MFA flow tests
    /// to avoid session contamination from other tests sharing fixture.Client.
    /// </summary>
    public HttpClient NewSessionClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Flushes the Dragonfly/Redis cache — resets rate limiters and session state between tests.</summary>
    public async Task FlushCacheAsync()
    {
        await _mux!.GetDatabase().ExecuteAsync("FLUSHALL");
    }

    /// <summary>Resolves a scoped service from the app's DI container.</summary>
    public T GetService<T>() where T : notnull
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Creates a new WebApplicationFactory with Smtp:Host configured (non-empty),
    /// registering the supplied email service stub. Caller must dispose the factory.
    /// </summary>
    public (HttpClient Client, WebApplicationFactory<Program> Factory)
        CreateSmtpEnabledClient(IEmailService emailService)
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"]           = _postgres.GetConnectionString(),
                        ["Cache:ConnectionString"]              = _redis.GetConnectionString(),
                        ["Cache:InstanceName"]                  = "test2:",
                        ["Cache:PatTtlMinutes"]                 = "5",
                        ["App:PublicUrl"]                       = "http://localhost",
                        ["App:Domain"]                          = "localhost",
                        ["App:AdminSpaOrigin"]                  = "http://localhost",
                        ["IAM_PUBLIC_PORT"]                     = "5000",
                        ["IAM_ADMIN_PORT"]                      = "5001",
                        ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                        ["Security:MaxLoginAttempts"]           = "5",
                        ["Security:LockoutMinutes"]             = "15",
                        ["Security:OtpTtlSeconds"]              = "300",
                        ["Security:MaxSmsPerWindow"]            = "3",
                        ["Security:SmsWindowMinutes"]           = "10",
                        ["Security:PatPrefix"]                  = "rediens_pat_",
                        ["Security:ArgonTimeCost"]              = "1",
                        ["Security:ArgonMemoryCost"]            = "8192",
                        ["Security:ArgonParallelism"]           = "1",
                        ["Hydra:AdminUrl"]                      = Hydra.Url,
                        ["Hydra:PublicUrl"]                     = Hydra.Url,
                        ["Keto:ReadUrl"]                        = Keto.ReadUrl,
                        ["Keto:WriteUrl"]                       = Keto.WriteUrl,
                        ["Smtp:Host"]                           = "smtp.test.local",
                        ["Smtp:FromAddress"]                    = "noreply@test.com",
                        ["Smtp:FromName"]                       = "Test IAM",
                        ["Bootstrap:Email"]                     = "",
                        ["Bootstrap:Password"]                  = "",
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    var ed = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
                    if (ed != null) services.Remove(ed);
                    services.AddSingleton<IEmailService>(emailService);
                    var sd = services.SingleOrDefault(d => d.ServiceType == typeof(ISmsService));
                    if (sd != null) services.Remove(sd);
                    services.AddSingleton<ISmsService>(SmsStub);
                    services.Configure<SessionOptions>(opts =>
                    {
                        opts.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        opts.Cookie.SameSite    = SameSiteMode.Unspecified;
                    });
                    DisableCircuitBreakers(services);
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        return (client, factory);
    }

    /// <summary>
    /// Creates a new WebApplicationFactory backed by the MailHog SMTP container and using the
    /// REAL SmtpEmailService (no stub replacement). Covers SmtpEmailService send paths.
    /// Caller must dispose the factory.
    /// </summary>
    public (HttpClient Client, WebApplicationFactory<Program> Factory) CreateRealSmtpClient()
    {
        var smtpPort = _mailhog.GetMappedPublicPort(1025);
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"]           = _postgres.GetConnectionString(),
                        ["Cache:ConnectionString"]              = _redis.GetConnectionString(),
                        ["Cache:InstanceName"]                  = "test3:",
                        ["Cache:PatTtlMinutes"]                 = "5",
                        ["App:PublicUrl"]                       = "http://localhost",
                        ["App:Domain"]                          = "localhost",
                        ["App:AdminSpaOrigin"]                  = "http://localhost",
                        ["IAM_PUBLIC_PORT"]                     = "5000",
                        ["IAM_ADMIN_PORT"]                      = "5001",
                        ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                        ["Security:MaxLoginAttempts"]           = "5",
                        ["Security:LockoutMinutes"]             = "15",
                        ["Security:OtpTtlSeconds"]              = "300",
                        ["Security:MaxSmsPerWindow"]            = "3",
                        ["Security:SmsWindowMinutes"]           = "10",
                        ["Security:PatPrefix"]                  = "rediens_pat_",
                        ["Security:ArgonTimeCost"]              = "1",
                        ["Security:ArgonMemoryCost"]            = "8192",
                        ["Security:ArgonParallelism"]           = "1",
                        ["Hydra:AdminUrl"]                      = Hydra.Url,
                        ["Hydra:PublicUrl"]                     = Hydra.Url,
                        ["Keto:ReadUrl"]                        = Keto.ReadUrl,
                        ["Keto:WriteUrl"]                       = Keto.WriteUrl,
                        // Real SMTP via MailHog — no IEmailService stub replacement below
                        ["Smtp:Host"]                           = "127.0.0.1",
                        ["Smtp:Port"]                           = smtpPort.ToString(),
                        ["Smtp:StartTls"]                       = "false",
                        ["Smtp:Username"]                       = "mailhog-user",
                        ["Smtp:Password"]                       = "mailhog-pass",
                        ["Smtp:FromAddress"]                    = "noreply@test.com",
                        ["Smtp:FromName"]                       = "Test IAM",
                        ["Bootstrap:Email"]                     = "",
                        ["Bootstrap:Password"]                  = "",
                    });
                });
                builder.ConfigureServices(services =>
                {
                    // Real SmtpEmailService is kept — do NOT replace IEmailService
                    services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    var sd = services.SingleOrDefault(d => d.ServiceType == typeof(ISmsService));
                    if (sd != null) services.Remove(sd);
                    services.AddSingleton<ISmsService>(SmsStub);
                    services.Configure<SessionOptions>(opts =>
                    {
                        opts.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        opts.Cookie.SameSite    = SameSiteMode.Unspecified;
                    });
                    DisableCircuitBreakers(services);
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        return (client, factory);
    }

    /// <summary>
    /// Creates a new WebApplicationFactory with IFido2 replaced by the supplied stub.
    /// The stub receives a fresh real Fido2 inner instance so RequestNewCredential /
    /// GetAssertionOptions still produce valid JSON-able option objects.
    /// Caller must dispose the factory.
    /// </summary>
    public (HttpClient Client, WebApplicationFactory<Program> Factory)
        CreateFido2MockClient(Fido2NetLib.IFido2 fido2Mock)
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"]           = _postgres.GetConnectionString(),
                        ["Cache:ConnectionString"]              = _redis.GetConnectionString(),
                        ["Cache:InstanceName"]                  = "test4:",
                        ["Cache:PatTtlMinutes"]                 = "5",
                        ["App:PublicUrl"]                       = "http://localhost",
                        ["App:Domain"]                          = "localhost",
                        ["App:AdminSpaOrigin"]                  = "http://localhost",
                        ["IAM_PUBLIC_PORT"]                     = "5000",
                        ["IAM_ADMIN_PORT"]                      = "5001",
                        ["Security:TotpSecretEncryptionKey"]    = new string('0', 64),
                        ["Security:MaxLoginAttempts"]           = "5",
                        ["Security:LockoutMinutes"]             = "15",
                        ["Security:OtpTtlSeconds"]              = "300",
                        ["Security:MaxSmsPerWindow"]            = "3",
                        ["Security:SmsWindowMinutes"]           = "10",
                        ["Security:PatPrefix"]                  = "rediens_pat_",
                        ["Security:ArgonTimeCost"]              = "1",
                        ["Security:ArgonMemoryCost"]            = "8192",
                        ["Security:ArgonParallelism"]           = "1",
                        ["Hydra:AdminUrl"]                      = Hydra.Url,
                        ["Hydra:PublicUrl"]                     = Hydra.Url,
                        ["Keto:ReadUrl"]                        = Keto.ReadUrl,
                        ["Keto:WriteUrl"]                       = Keto.WriteUrl,
                        ["Smtp:Host"]                           = "smtp.test.local",
                        ["Smtp:FromAddress"]                    = "noreply@test.com",
                        ["Smtp:FromName"]                       = "Test IAM",
                        ["Bootstrap:Email"]                     = "",
                        ["Bootstrap:Password"]                  = "",
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    var ed = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
                    if (ed != null) services.Remove(ed);
                    services.AddSingleton<IEmailService>(EmailStub);
                    var sd = services.SingleOrDefault(d => d.ServiceType == typeof(ISmsService));
                    if (sd != null) services.Remove(sd);
                    services.AddSingleton<ISmsService>(SmsStub);
                    // Replace IFido2 with the caller-supplied mock
                    var fd = services.SingleOrDefault(d => d.ServiceType == typeof(Fido2NetLib.IFido2));
                    if (fd != null) services.Remove(fd);
                    services.AddScoped<Fido2NetLib.IFido2>(_ => fido2Mock);
                    services.Configure<SessionOptions>(opts =>
                    {
                        opts.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        opts.Cookie.SameSite    = SameSiteMode.Unspecified;
                    });
                    DisableCircuitBreakers(services);
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        return (client, factory);
    }

    /// <summary>Refreshes the Db context (clears EF Core's first-level cache).</summary>
    public async Task RefreshDbAsync()
    {
        foreach (var entry in Db.ChangeTracker.Entries().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        await Task.CompletedTask;
    }

    // Polly's standard resilience handler includes a circuit breaker. Tests that simulate
    // Hydra/Keto failures generate many 500s with retries, which can trip the circuit
    // and cause all subsequent Hydra introspect calls to fail with BrokenCircuitException.
    // Raise MinimumThroughput to int.MaxValue so the circuit never opens in tests.
    private static void DisableCircuitBreakers(IServiceCollection services) =>
        services.PostConfigureAll<HttpStandardResilienceOptions>(opts =>
            opts.CircuitBreaker.MinimumThroughput = int.MaxValue);
}

// ── Stub email service ────────────────────────────────────────────────────────

/// <summary>
/// Records sent emails in memory so tests can assert on them without real SMTP.
/// </summary>
public class StubEmailService : IEmailService
{
    public List<SentEmail>   SentEmails   { get; } = [];
    public List<SentInvite>  SentInvites  { get; } = [];

    /// <summary>When set, the next SendOtpAsync call throws this exception then clears itself.</summary>
    public Exception? ThrowOnNextSend { get; set; }

    public Task SendOtpAsync(string to, string code, string purpose,
        Guid? orgId = null, Guid? projectId = null)
    {
        if (ThrowOnNextSend != null)
        {
            var ex = ThrowOnNextSend;
            ThrowOnNextSend = null;
            throw ex;
        }
        SentEmails.Add(new SentEmail(to, purpose, code));
        return Task.CompletedTask;
    }

    public Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null)
    {
        SentInvites.Add(new SentInvite(to, inviteUrl, orgName));
        return Task.CompletedTask;
    }

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt, Guid? orgId = null)
    {
        NewDeviceAlerts.Add(new SentNewDeviceAlert(to, ipAddress));
        return Task.CompletedTask;
    }

    public Task CheckConnectivityAsync() => Task.CompletedTask;

    public List<SentNewDeviceAlert> NewDeviceAlerts { get; } = [];
}

public record SentEmail(string To, string Purpose, string Code);
public record SentInvite(string To, string InviteUrl, string OrgName);
public record SentNewDeviceAlert(string To, string IpAddress);

/// <summary>Captures SMS OTP codes so tests can complete the MFA flow.</summary>
public class StubSmsService : ISmsService
{
    public List<SentSms> SentMessages { get; } = [];

    public Task SendOtpAsync(string to, string code, string purpose)
    {
        SentMessages.Add(new SentSms(to, purpose, code));
        return Task.CompletedTask;
    }
}

public record SentSms(string To, string Purpose, string Code);

// ── HIBP (Have I Been Pwned) stub handler ────────────────────────────────────

/// <summary>
/// Primary HttpMessageHandler for the unnamed IHttpClientFactory client.
/// When configured via <see cref="Setup"/>, it intercepts requests to
/// api.pwnedpasswords.com and returns a fake range response with the exact
/// suffix/count pair for the configured password.  All other hosts get an
/// empty 200 so other unnamed-client callers (SocialLoginService) don't crash.
/// Call <see cref="Clear"/> after each test to restore passthrough behaviour.
/// </summary>
public class HibpStubHandler : HttpMessageHandler
{
    private string? _matchSuffix;
    private int     _matchCount;

    // ── GitHub / social-login stubs ───────────────────────────────────────────

    private string? _githubToken;
    private long    _githubUserId;
    private string? _githubEmail;
    private string? _githubName;

    /// <summary>Configures the stub to report <paramref name="count"/> breaches for <paramref name="password"/>.</summary>
    public void Setup(string password, int count = 100)
    {
        var sha1 = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        _matchSuffix = Convert.ToHexString(sha1).ToUpperInvariant()[5..]; // 35-char suffix
        _matchCount  = count;
    }

    /// <summary>Clears the stub so it returns count=0 for all passwords (fail-open).</summary>
    public void Clear() { _matchSuffix = null; _matchCount = 0; }

    /// <summary>
    /// Configures stub to return a GitHub access-token response and user profile.
    /// Intercepted URLs: POST github.com (token exchange) and GET api.github.com (user profile).
    /// </summary>
    public void SetupGithubProfile(long userId, string email, string name = "Stub User")
    {
        _githubToken  = "stub-gh-access-token";
        _githubUserId = userId;
        _githubEmail  = email;
        _githubName   = name;
    }

    /// <summary>Clears GitHub stub back to default (empty body → exchange fails).</summary>
    public void ClearGithub()
    {
        _githubToken  = null;
        _githubUserId = 0;
        _githubEmail  = null;
        _githubName   = null;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "";
        string body = "";

        if (_matchSuffix != null && host == "api.pwnedpasswords.com")
        {
            body = $"{_matchSuffix}:{_matchCount}\n";
        }
        else if (_githubToken != null && host == "github.com")
        {
            // GitHub token-exchange response
            body = System.Text.Json.JsonSerializer.Serialize(new { access_token = _githubToken });
        }
        else if (_githubToken != null && host == "api.github.com")
        {
            // GitHub user-profile or emails endpoint
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.StartsWith("/user/emails", StringComparison.OrdinalIgnoreCase))
            {
                // Return primary+verified email
                body = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { email = _githubEmail, primary = true, verified = true }
                });
            }
            else
            {
                body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id    = _githubUserId,
                    email = _githubEmail,
                    name  = _githubName,
                    login = (_githubName ?? "stub").ToLowerInvariant().Replace(" ", "")
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(body,
                System.Text.Encoding.UTF8, "application/json")
        });
    }
}

// Sets RemoteIpAddress to loopback for all test requests (TestServer leaves it null)
internal sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((ctx, nxt) =>
        {
            ctx.Connection.RemoteIpAddress ??= System.Net.IPAddress.Loopback;
            return nxt(ctx);
        });
        next(app);
    };
}

/// <summary>Bypasses SSRF checks so tests can deliver webhooks to local WireMock servers.</summary>
file sealed class PassthroughSsrfValidator : IWebhookSsrfValidator
{
    public Task<bool> IsPrivateOrReservedAsync(string url) => Task.FromResult(false);
}
