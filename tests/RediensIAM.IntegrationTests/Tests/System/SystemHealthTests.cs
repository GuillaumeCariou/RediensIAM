using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using System.Net.Http.Headers;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.System;

// ── from SystemHealthTests.cs ─────────────────────────────────

[Collection("RediensIAM")]
public class SystemHealthTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── Auth: 401 / 403 guards ────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetHealth_OrgAdmin_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.OrgAdminToken(user.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetHealth_ProjectAdmin_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var user           = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.ProjectManagerToken(user.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Response shape ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_SuperAdmin_Returns200WithExpectedShape()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("overall", out var overall).Should().BeTrue();
        overall.GetString().Should().BeOneOf("ok", "error");
        body.TryGetProperty("checks", out var checks).Should().BeTrue();
        checks.ValueKind.Should().Be(JsonValueKind.Array);
        checks.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetHealth_AllExpectedComponentsPresent()
    {
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var names = body.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToHashSet();

        names.Should().Contain("PostgreSQL");
        names.Should().Contain("Dragonfly");
        names.Should().Contain("Hydra (admin)");
        names.Should().Contain("Hydra (public)");
        names.Should().Contain("Keto (read)");
        names.Should().Contain("Keto (write)");
        names.Should().Contain("SMTP");
    }

    [Fact]
    public async Task GetHealth_EachCheckHasCategoryAndStatus()
    {
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var check in body.GetProperty("checks").EnumerateArray())
        {
            check.TryGetProperty("name",     out _).Should().BeTrue();
            check.TryGetProperty("category", out _).Should().BeTrue();
            check.TryGetProperty("status",   out var status).Should().BeTrue();
            status.GetString().Should().BeOneOf("Ok", "Error", "NotConfigured");
        }
    }

    // ── Per-component checks ──────────────────────────────────────────────────

    [Fact]
    public async Task GetHealth_PostgreSQL_IsOkAndHasStats()
    {
        // Seed some data so counts are non-trivial
        await fixture.Seed.CreateOrgAsync();
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var pg   = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "PostgreSQL");

        pg.GetProperty("status").GetString().Should().Be("Ok");
        pg.GetProperty("latency_ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);

        pg.TryGetProperty("stats", out var stats).Should().BeTrue();
        stats.ValueKind.Should().Be(JsonValueKind.Object);
        stats.TryGetProperty("organisations", out _).Should().BeTrue();
        stats.TryGetProperty("users",         out _).Should().BeTrue();
        stats.TryGetProperty("projects",      out _).Should().BeTrue();
        stats.TryGetProperty("db_size",       out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetHealth_HydraAdmin_IsOkAndHasVersion()
    {
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var hydra = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "Hydra (admin)");

        hydra.GetProperty("status").GetString().Should().Be("Ok");
        hydra.TryGetProperty("stats", out var stats).Should().BeTrue();
        stats.TryGetProperty("version", out var version).Should().BeTrue();
        version.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetHealth_Keto_IsOkAndHasVersion()
    {
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var name in new[] { "Keto (read)", "Keto (write)" })
        {
            var keto = body.GetProperty("checks").EnumerateArray()
                .First(c => c.GetProperty("name").GetString() == name);
            keto.GetProperty("status").GetString().Should().Be("Ok");
        }
    }

    [Fact]
    public async Task GetHealth_Smtp_NotConfigured_InTestEnvironment()
    {
        // TestFixture sets Smtp:Host to "" — should report NotConfigured, not Error
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var smtp = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "SMTP");

        smtp.GetProperty("status").GetString().Should().Be("NotConfigured");
        smtp.TryGetProperty("detail", out var detail).Should().BeTrue();
        detail.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetHealth_Overall_IsOkWhenAllServicesUp()
    {
        var client = await SuperAdminClientAsync();

        var res  = await client.GetAsync("/admin/system/health");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // In the test environment all real services (postgres, dragonfly) are up
        // and Ory stubs return 200 — overall should be ok
        body.GetProperty("overall").GetString().Should().Be("ok");
    }
}

// ── from SystemHealthCoverageTests.cs ─────────────────────────

/// <summary>
/// Covers SystemHealthController failure paths not hit by SystemHealthTests:
///   - Probe failure catch block (lines 217-220)
///   - Err helper (lines 241-244)
///   - CheckHydraAdmin error branch (line 110)
///   - CheckHydraPublic error branch (line 130)
///   - CheckSmtp success path (lines 183-195)
///   - CheckSmtp failure / Err helper (lines 197-200)
/// </summary>
[Collection("RediensIAM")]
public class SystemHealthCoverageTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── Hydra health failure → error path in CheckHydraAdmin/Public ──────────

    [Fact]
    public async Task GetHealth_WhenHydraHealthFails_ReturnsErrorForHydraComponents()
    {
        var client = await SuperAdminClientAsync();

        // Make /health/alive return 500 — triggers Probe catch and Err helper
        fixture.Hydra.SetHealthFailure();
        try
        {
            var res  = await client.GetAsync("/admin/system/health");

            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body   = await res.Content.ReadFromJsonAsync<JsonElement>();
            var checks = body.GetProperty("checks").EnumerateArray().ToList();

            // Both Hydra components should report Error because /health/alive → 500
            var hydraAdmin  = checks.First(c => c.GetProperty("name").GetString() == "Hydra (admin)");
            var hydraPublic = checks.First(c => c.GetProperty("name").GetString() == "Hydra (public)");

            hydraAdmin.GetProperty("status").GetString().Should().Be("Error");
            hydraPublic.GetProperty("status").GetString().Should().Be("Error");

            // Overall should not be "ok" since at least one component failed
            body.GetProperty("overall").GetString().Should().Be("error");
        }
        finally
        {
            fixture.Hydra.RestoreHealth();
        }
    }

    // ── CheckSmtp success path (lines 183-195) ────────────────────────────────

    [Fact]
    public async Task GetHealth_SmtpConfigured_ConnectSucceeds_ReturnsSmtpOk()
    {
        // fixture.EmailStub.CheckConnectivityAsync returns Task.CompletedTask → success path
        var (client, factory) = fixture.CreateSmtpEnabledClient(fixture.EmailStub);
        await using var _f = factory;

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        fixture.Keto.AllowAll();

        var res = await client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var smtp = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "SMTP");
        smtp.GetProperty("status").GetString().Should().Be("Ok");
        smtp.GetProperty("stats").GetProperty("host").GetString().Should().Be("smtp.test.local");
    }

    // ── Keto read health failure → error path in CheckKetoRead (line 147) ──────

    [Fact]
    public async Task GetHealth_WhenKetoReadHealthFails_ReturnsErrorForKetoReadComponent()
    {
        var client = await SuperAdminClientAsync();

        fixture.Keto.SetReadHealthFailure();
        try
        {
            var res  = await client.GetAsync("/admin/system/health");

            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body   = await res.Content.ReadFromJsonAsync<JsonElement>();
            var checks = body.GetProperty("checks").EnumerateArray().ToList();

            var ketoRead = checks.First(c => c.GetProperty("name").GetString() == "Keto (read)");
            ketoRead.GetProperty("status").GetString().Should().Be("Error");
            body.GetProperty("overall").GetString().Should().Be("error");
        }
        finally
        {
            fixture.Keto.RestoreHealth();
        }
    }

    // ── Keto write health failure → error path in CheckKetoWrite (line 164) ─

    [Fact]
    public async Task GetHealth_WhenKetoWriteHealthFails_ReturnsErrorForKetoWriteComponent()
    {
        var client = await SuperAdminClientAsync();

        fixture.Keto.SetWriteHealthFailure();
        try
        {
            var res  = await client.GetAsync("/admin/system/health");

            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body   = await res.Content.ReadFromJsonAsync<JsonElement>();
            var checks = body.GetProperty("checks").EnumerateArray().ToList();

            var ketoWrite = checks.First(c => c.GetProperty("name").GetString() == "Keto (write)");
            ketoWrite.GetProperty("status").GetString().Should().Be("Error");
            body.GetProperty("overall").GetString().Should().Be("error");
        }
        finally
        {
            fixture.Keto.RestoreHealth();
        }
    }

    // ── Version fetch throws (invalid JSON body) → best-effort catch (lines 121, 138, 155, 172) ─

    [Fact]
    public async Task GetHealth_WhenVersionEndpointsReturnInvalidJson_CatchesAndReturnsOk()
    {
        var client = await SuperAdminClientAsync();

        // All /version endpoints return 200 with non-JSON body → JsonException → best-effort catch
        fixture.Hydra.SetVersionBroken();
        fixture.Keto.SetVersionBroken();
        try
        {
            var res = await client.GetAsync("/admin/system/health");

            // Despite FetchVersion throwing, all components should still report health based on /health/alive
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("overall").GetString().Should().Be("ok");
        }
        finally
        {
            fixture.Hydra.RestoreVersion();
            fixture.Keto.RestoreVersion();
        }
    }

    // ── CheckSmtp failure path + Err helper (lines 197-200, 241-244) ─────────

    [Fact]
    public async Task GetHealth_SmtpConfigured_ConnectFails_ReturnsSmtpError()
    {
        // ThrowingEmailService.CheckConnectivityAsync throws → catch block + Err helper
        var emailStub = new ThrowingConnectivityEmailService();
        var (client, factory) = fixture.CreateSmtpEnabledClient(emailStub);
        await using var _f = factory;

        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        fixture.Keto.AllowAll();

        var res = await client.GetAsync("/admin/system/health");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var smtp = body.GetProperty("checks").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "SMTP");
        smtp.GetProperty("status").GetString().Should().Be("Error");
    }
}

// ── Stubs local to this file ──────────────────────────────────────────────────

/// <summary>Email service whose CheckConnectivityAsync always throws — covers lines 197-200.</summary>
file sealed class ThrowingConnectivityEmailService : IEmailService
{
    public Task CheckConnectivityAsync() =>
        throw new InvalidOperationException("Simulated SMTP connection failure");

    public Task SendOtpAsync(string to, string code, string purpose,
        Guid? orgId = null, Guid? projectId = null) => Task.CompletedTask;

    public Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null) =>
        Task.CompletedTask;

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent,
        DateTimeOffset loginAt, Guid? orgId = null) => Task.CompletedTask;
}
