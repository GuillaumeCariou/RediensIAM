using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Org;

/// <summary>
/// Covers OrgController lines not hit by existing test files:
///   - GET /org/userlists/{id}/users/{uid}/sessions (lines 553-565)
///   - DELETE /org/userlists/{id}/users/{uid}/sessions (lines 569-577)
///   - POST /org/smtp/test failure path (lines 756-759)
///   - GET /org/userlists/{id}/export rate-limit (line 862)
///   - GET /org/userlists/{id}/export CSV format (line 884)
/// </summary>
[Collection("RediensIAM")]
public class OrgCoverageTests(TestFixture fixture)
{
    private async Task<(Organisation org, UserList list, User user, HttpClient client)> ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var list = await fixture.Seed.CreateUserListAsync(org.Id);
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        return (org, list, user, client);
    }

    // ── GET /org/userlists/{id}/users/{uid}/sessions ──────────────────────────

    [Fact]
    public async Task ListUserSessions_ExistingUser_Returns200WithArray()
    {
        var (_, list, user, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListUserSessions_NonExistentUser_Returns404()
    {
        var (_, list, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /org/userlists/{id}/users/{uid}/sessions ───────────────────────

    [Fact]
    public async Task RevokeUserSessions_ExistingUser_Returns200()
    {
        var (_, list, user, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{user.Id}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("sessions_revoked");
    }

    [Fact]
    public async Task RevokeUserSessions_NonExistentUser_Returns404()
    {
        var (_, list, _, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/org/userlists/{list.Id}/users/{Guid.NewGuid()}/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /org/smtp/test — failure path (lines 756-759) ───────────────────

    [Fact]
    public async Task TestSmtp_WhenEmailServiceThrows_Returns400WithSmtpTestFailed()
    {
        var (_, _, _, client) = await ScaffoldAsync();

        fixture.EmailStub.ThrowOnNextSend = new InvalidOperationException("Connection refused");

        var res = await client.PostAsync("/org/smtp/test", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("smtp_test_failed");
    }

    // ── GET /org/userlists/{id}/export — rate limit ───────────────────────────

    [Fact]
    public async Task ExportUserList_SecondCallInWindow_Returns429()
    {
        var (_, list, _, client) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        var first = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");
        second.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("export_rate_limited");
    }

    // ── GET /org/userlists/{id}/export — CSV with special chars ──────────────

    [Fact]
    public async Task ExportUserList_UserWithCommaInDisplayName_ReturnsCsvWithQuoting()
    {
        var (_, list, user, client) = await ScaffoldAsync();
        await fixture.FlushCacheAsync();

        user.DisplayName = "Smith, John";
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await res.Content.ReadAsStringAsync();
        csv.Should().Contain("\"Smith, John\"");
    }
}
