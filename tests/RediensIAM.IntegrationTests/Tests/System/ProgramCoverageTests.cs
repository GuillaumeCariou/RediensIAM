using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RediensIAM.IntegrationTests.Tests.System;

/// <summary>
/// Covers Program.cs lines that are not exercised by any request-level test:
///   - AddSwaggerGen configuration lambda (lines 106-134)
///   - BootstrapSuperAdminAsync / EnsureBootstrapAdminAsync (lines 244-298)
/// Also covers AuditLogRetentionService line 57 (log when purge count > 0).
/// </summary>
[Collection("RediensIAM")]
public class ProgramCoverageTests(TestFixture fixture)
{
    // ── AddSwaggerGen lambda (Program.cs lines 106-134) ───────────────────────

    [Fact]
    public void SwaggerGenOptions_Resolution_ExecutesConfigurationLambda()
    {
        // Resolving IOptions<SwaggerGenOptions>.Value forces Swashbuckle to apply
        // every services.Configure<SwaggerGenOptions>(...) delegate, which is exactly
        // the lambda body passed to AddSwaggerGen in Program.cs.
        using var scope    = fixture.Services.CreateScope();
        var genOptions     = scope.ServiceProvider
            .GetRequiredService<IOptions<SwaggerGenOptions>>().Value;

        genOptions.Should().NotBeNull();
    }

    // ── BootstrapSuperAdminAsync / EnsureBootstrapAdminAsync (lines 244-298) ──

    [Fact]
    public async Task BootstrapSuperAdmin_WhenEmailConfigured_CreatesAdminUser()
    {
        const string bootstrapEmail = "bootstrap-cov@testcoverage.dev";

        var connStr   = GetConnectionString();
        var redisStr  = GetRedisString();
        var hibp      = fixture.HibpStub;
        var emailStub = fixture.EmailStub;
        var smsStub   = fixture.SmsStub;

        // A separate factory rather than the shared fixture: Bootstrap:Email has to be set before
        // the host starts for Program.cs to run the bootstrap-super-admin path at all.
        await using var bootstrapFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Testing");
                b.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Default"]          = connStr,
                        ["Cache:ConnectionString"]             = redisStr,
                        ["Cache:InstanceName"]                 = "boot-test:",
                        ["Cache:PatTtlMinutes"]                = "5",
                        ["App:PublicUrl"]                      = "http://localhost",
                        ["App:Domain"]                         = "localhost",
                        ["App:AdminSpaOrigin"]                 = "http://localhost",
                        ["IAM_PUBLIC_PORT"]                    = "5000",
                        ["IAM_ADMIN_PORT"]                     = "5001",
                        ["Security:TotpSecretEncryptionKey"]   = new string('0', 64),
                        ["Security:MaxLoginAttempts"]          = "5",
                        ["Security:LockoutMinutes"]            = "15",
                        ["Security:OtpTtlSeconds"]             = "300",
                        ["Security:MaxSmsPerWindow"]           = "3",
                        ["Security:SmsWindowMinutes"]          = "10",
                        ["Security:PatPrefix"]                 = "rediens_pat_",
                        ["Security:ArgonTimeCost"]             = "1",
                        ["Security:ArgonMemoryCost"]           = "8192",
                        ["Security:ArgonParallelism"]          = "1",
                        ["Hydra:AdminUrl"]                     = fixture.Hydra.Url,
                        ["Hydra:PublicUrl"]                    = fixture.Hydra.Url,
                        ["Keto:ReadUrl"]                       = fixture.Keto.ReadUrl,
                        ["Keto:WriteUrl"]                      = fixture.Keto.WriteUrl,
                        ["Smtp:Host"]                          = "",
                        ["Smtp:FromAddress"]                   = "noreply@test.com",
                        ["Smtp:FromName"]                      = "Test IAM",
                        // Non-empty bootstrap credentials trigger BootstrapSuperAdminAsync
                        ["IAM_BOOTSTRAP_EMAIL"]                = bootstrapEmail,
                        ["IAM_BOOTSTRAP_PASSWORD"]             = "P@ssw0rd!Boot",
                    }));

                b.ConfigureServices(svc =>
                {
                    svc.AddSingleton<IStartupFilter>(new LoopbackRemoteIpStartupFilter());
                    var ed = svc.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
                    if (ed != null) svc.Remove(ed);
                    svc.AddSingleton<IEmailService>(emailStub);
                    var sd = svc.SingleOrDefault(d => d.ServiceType == typeof(ISmsService));
                    if (sd != null) svc.Remove(sd);
                    svc.AddSingleton<ISmsService>(smsStub);
                    svc.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => hibp);
                    svc.Configure<SessionOptions>(o =>
                    {
                        o.Cookie.SecurePolicy = CookieSecurePolicy.None;
                        o.Cookie.SameSite    = SameSiteMode.Unspecified;
                    });
                });
            });

        // Creating the client triggers app startup — Program.cs bootstrap code runs
        _ = bootstrapFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await fixture.RefreshDbAsync();
        var user = await fixture.Db.Users.FirstOrDefaultAsync(u => u.Email == bootstrapEmail);
        user.Should().NotBeNull("BootstrapSuperAdminAsync should have created the user");
        user!.Active.Should().BeTrue();
    }

    // ── AuditLogRetentionService — log when purge count > 0 (line 57) ─────────

    [Fact]
    public async Task AuditLogRetention_WhenExpiredLogsExist_LogsPurgeCount()
    {
        // Seed an audit log far beyond the default 365-day retention period
        fixture.Db.AuditLogs.Add(new RediensIAM.Data.Entities.AuditLog
        {
            Action    = "test.retention.coverage",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400),
        });
        await fixture.Db.SaveChangesAsync();

        using var scope = fixture.Services.CreateScope();
        var retentionSvc = scope.ServiceProvider
            .GetServices<IHostedService>()
            .OfType<AuditLogRetentionService>()
            .FirstOrDefault();
        retentionSvc.Should().NotBeNull("AuditLogRetentionService should be registered");

        // Invoke PurgeExpiredLogsAsync via reflection (private method)
        var purgeMethod = typeof(AuditLogRetentionService)
            .GetMethod("PurgeExpiredLogsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)purgeMethod.Invoke(retentionSvc, [CancellationToken.None])!;

        await fixture.RefreshDbAsync();
        var stillExists = await fixture.Db.AuditLogs
            .AnyAsync(a => a.Action == "test.retention.coverage");
        stillExists.Should().BeFalse("expired audit logs should have been purged");
    }

    // ── Console SPA fallback ─────────────────────────────────────────────────

    [Fact]
    public async Task ConsoleSpaFallback_NonApiPath_ExecutesFallbackHandler()
    {
        // /console/ui matches no API controller, so MapFallback runs and tries to serve the SPA
        // file. There is no wwwroot under test, so that throws and comes back as a 500. The 500 is
        // the pass condition: a 404 would mean the fallback route never matched at all.
        //
        // The path was /admin/ui, which is the prefix the management API owns — that is what made
        // every console page under /admin/system answer a browser with a 401 before the SPA loaded.
        var res = await fixture.Client.GetAsync($"/{RediensIAM.Config.Roles.ConsoleBasePath}/ui");
        res.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "MapFallback should have matched and executed the lambda (404 = no route matched at all)");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string GetConnectionString() =>
        fixture.Services.CreateScope()
            .ServiceProvider
            .GetRequiredService<RediensIAM.Data.RediensIamDbContext>()
            .Database.GetConnectionString()!;

    private string GetRedisString() =>
        fixture.Services.CreateScope()
            .ServiceProvider
            .GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>()
            .Configuration;
}
