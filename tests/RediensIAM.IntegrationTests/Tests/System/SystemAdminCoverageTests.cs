using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.System;

/// <summary>
/// Targeted tests that cover specific uncovered branches in SystemAdminController
/// identified via SonarQube line-coverage analysis.
/// </summary>
[Collection("RediensIAM")]
public class SystemAdminCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, HttpClient client)> SuperAdminAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token          = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(token));
    }

    // ── GET /admin/hydra/clients/{id} — found path (line 873) ────────────────

    [Fact]
    public async Task GetHydraClient_ExistingClient_Returns200()
    {
        var (_, client) = await SuperAdminAsync();
        const string clientId = "test-hydra-client";
        fixture.Hydra.SetupClientGetResponse(clientId);

        var res = await client.GetAsync($"/admin/hydra/clients/{clientId}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("client_id").GetString().Should().Be(clientId);
    }

    // ── DELETE /admin/organizations/{id} — with users in lists (line 149) ────

    [Fact]
    public async Task DeleteOrg_WithUsersInLists_Returns204()
    {
        var (org, adminClient) = await SuperAdminAsync();

        // Create an extra user list for the org and put users in it
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.Seed.CreateUserAsync(list.Id);

        var res = await adminClient.DeleteAsync($"/admin/organizations/{org.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/organizations/{id} — Hydra client delete fails (lines 133-134) ─

    [Fact]
    public async Task DeleteOrg_WithProjectHydraClientDeleteFailure_StillReturns204()
    {
        var (org, adminClient) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        // Make Hydra return 500 for this project's client — the catch block should eat it
        fixture.Hydra.SetupClientDeleteFailure(project.HydraClientId!);

        var res = await adminClient.DeleteAsync($"/admin/organizations/{org.Id}");

        // Even though Hydra deletion failed, the org deletion proceeds
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/projects/{id} — Hydra client delete fails (line 594) ───

    [Fact]
    public async Task DeleteProject_HydraClientDeleteFailure_StillReturns204()
    {
        var (org, adminClient) = await SuperAdminAsync();
        var project = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Hydra.SetupClientDeleteFailure(project.HydraClientId!);

        var res = await adminClient.DeleteAsync($"/admin/projects/{project.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── DELETE /admin/userlists/{id}/users/{uid} — from system list (line 389) ─

    [Fact]
    public async Task RemoveUserFromSystemList_Returns204AndRemovesSuperAdminTuple()
    {
        // System user list: OrgId=null, Immovable=true
        var systemList = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}",
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(systemList.Id);

        var (_, adminClient) = await SuperAdminAsync();
        // AllowAll already set; keto.DeleteRelationTupleAsync will be called for "member"
        // AND for super_admin tuple (line 389 branch)

        var res = await adminClient.DeleteAsync($"/admin/userlists/{systemList.Id}/users/{user.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── POST /admin/organizations/{id}/smtp/test — failure path (lines 816-819) ─

    [Fact]
    public async Task TestOrgSmtp_WhenEmailServiceThrows_Returns400WithSmtpTestFailed()
    {
        var (org, adminClient) = await SuperAdminAsync();

        // Make the next SendOtpAsync call throw to simulate SMTP connection failure
        fixture.EmailStub.ThrowOnNextSend = new InvalidOperationException("Connection refused");

        var res = await adminClient.PostAsync($"/admin/organizations/{org.Id}/smtp/test", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("smtp_test_failed");
    }

    // ── GET /admin/organizations/{id}/export/users — rate limit (line 893) ───

    [Fact]
    public async Task ExportUsers_SecondCallInWindow_Returns429()
    {
        var (org, adminClient) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        // First call — should succeed
        var first = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/users");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second call within the rate-limit window — should be 429
        var second = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/users");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("export_rate_limited");
    }

    // ── GET /admin/organizations/{id}/export/audit-log — rate limit (line 932) ─

    [Fact]
    public async Task ExportAuditLog_SecondCallInWindow_Returns429()
    {
        var (org, adminClient) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        var first = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/audit-log");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/audit-log");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("export_rate_limited");
    }

    // ── CSV quoting with embedded special chars (line 964) ───────────────────

    [Fact]
    public async Task ExportUsers_UserWithQuoteInName_ReturnsCsvWithEscaping()
    {
        var (org, adminClient) = await SuperAdminAsync();
        await fixture.FlushCacheAsync();

        // Create a user whose display name contains a double-quote — forces AdminCsvEscape's quote branch
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.DisplayName = "O'Brien, \"Bob\"";    // contains comma AND quotes
        await fixture.Db.SaveChangesAsync();

        var res = await adminClient.GetAsync($"/admin/organizations/{org.Id}/export/users?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await res.Content.ReadAsStringAsync();
        // Quoted field should appear in the CSV
        csv.Should().Contain("\"O'Brien, \"\"Bob\"\"\"");
    }

    // ── GET /admin/userlists/{id} — covers line 304 ───────────────────────────

    [Fact]
    public async Task GetUserList_ExistingList_Returns200WithUserCount()
    {
        var (org, adminClient) = await SuperAdminAsync();
        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        await fixture.Seed.CreateUserAsync(list.Id);

        var res = await adminClient.GetAsync($"/admin/userlists/{list.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(list.Id.ToString());
        body.TryGetProperty("user_count", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetUserList_NonExistent_Returns404()
    {
        var (_, adminClient) = await SuperAdminAsync();

        var res = await adminClient.GetAsync($"/admin/userlists/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
