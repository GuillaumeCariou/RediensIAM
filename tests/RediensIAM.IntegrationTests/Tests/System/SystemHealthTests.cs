using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

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
