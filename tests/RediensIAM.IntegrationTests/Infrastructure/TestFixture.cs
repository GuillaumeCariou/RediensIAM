using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    // ── Stubs ─────────────────────────────────────────────────────────────────
    public HydraStub        Hydra      { get; } = new();
    public KetoStub         Keto       { get; } = new();
    public StubEmailService EmailStub  { get; } = new();
    public StubSmsService   SmsStub    { get; } = new();

    // ── Cache connection (for flush between tests) ────────────────────────────
    private IConnectionMultiplexer _mux = null!;

    // ── App under test ────────────────────────────────────────────────────────
    private WebApplicationFactory<Program> _factory = null!;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Direct DB access for seeding and assertions.</summary>
    public RediensIamDbContext Db { get; private set; } = null!;

    /// <summary>Helper for creating test data.</summary>
    public SeedData Seed { get; private set; } = null!;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        // Start containers in parallel
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

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

                    // Allow session cookies over plain HTTP in tests (test server uses http://localhost)
                    services.Configure<SessionOptions>(opts =>
                    {
                        opts.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        opts.Cookie.SameSite    = SameSiteMode.Unspecified;
                    });
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
        _mux.Dispose();
        Client.Dispose();
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
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
        await _mux.GetDatabase().ExecuteAsync("FLUSHALL");
    }

    /// <summary>Refreshes the Db context (clears EF Core's first-level cache).</summary>
    public async Task RefreshDbAsync()
    {
        foreach (var entry in Db.ChangeTracker.Entries().ToList())
            entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        await Task.CompletedTask;
    }
}

// ── Stub email service ────────────────────────────────────────────────────────

/// <summary>
/// Records sent emails in memory so tests can assert on them without real SMTP.
/// </summary>
public class StubEmailService : IEmailService
{
    public List<SentEmail>   SentEmails   { get; } = [];
    public List<SentInvite>  SentInvites  { get; } = [];

    public Task SendOtpAsync(string to, string code, string purpose,
        Guid? orgId = null, Guid? projectId = null)
    {
        SentEmails.Add(new SentEmail(to, purpose, code));
        return Task.CompletedTask;
    }

    public Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null)
    {
        SentInvites.Add(new SentInvite(to, inviteUrl, orgName));
        return Task.CompletedTask;
    }

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt)
    {
        NewDeviceAlerts.Add(new SentNewDeviceAlert(to, ipAddress));
        return Task.CompletedTask;
    }

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

// Sets RemoteIpAddress to loopback for all test requests (TestServer leaves it null)
file sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
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
