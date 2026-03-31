using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

[Collection("RediensIAM")]
public class SystemAdminMiscTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminClientAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin        = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token        = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    // ── GET /admin/audit-log ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAuditLog_SuperAdmin_Returns200WithArray()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/audit-log");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetAuditLog_Unauthenticated_Returns401Or403()
    {
        var res = await fixture.Client.GetAsync("/admin/audit-log");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetAuditLog_LimitAndOffset_Returns200()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/audit-log?limit=5&offset=0");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeLessThanOrEqualTo(5);
    }

    // ── GET /admin/metrics ────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetrics_SuperAdmin_Returns200WithCounts()
    {
        var client = await SuperAdminClientAsync();

        var res = await client.GetAsync("/admin/metrics");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("org_count", out _).Should().BeTrue();
        body.TryGetProperty("active_users", out _).Should().BeTrue();
        body.TryGetProperty("project_count", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetMetrics_CountsReflectSeededData()
    {
        var client = await SuperAdminClientAsync();

        // Seed one extra org to ensure org_count >= 1
        await fixture.Seed.CreateOrgAsync();

        var res  = await client.GetAsync("/admin/metrics");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("org_count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("active_users").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetMetrics_Unauthenticated_Returns401Or403()
    {
        var res = await fixture.Client.GetAsync("/admin/metrics");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
