using System.Net.Http.Headers;
using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.System;

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
        DateTimeOffset loginAt) => Task.CompletedTask;
}
