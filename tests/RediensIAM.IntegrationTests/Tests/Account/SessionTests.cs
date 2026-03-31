using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Account;

[Collection("RediensIAM")]
public class SessionTests(TestFixture fixture)
{
    private async Task<(User user, HttpClient client)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        return (user, fixture.ClientWithToken(token));
    }

    // ── GET /account/sessions ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSessions_Authenticated_Returns200WithList()
    {
        var (user, client) = await ScaffoldAsync();
        fixture.Hydra.SetupConsentSessions(user.Id.ToString(), [
            new { client_id = "app1", granted_at = DateTimeOffset.UtcNow.AddDays(-1).ToString("o") }
        ]);

        var res = await client.GetAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetSessions_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── DELETE /account/sessions ──────────────────────────────────────────────

    [Fact]
    public async Task RevokeAllSessions_Authenticated_Returns200()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokeAllSessions_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── DELETE /account/sessions/{clientId} ───────────────────────────────────

    [Fact]
    public async Task RevokeSession_Authenticated_Returns200()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync("/account/sessions/some-client-id");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokeSession_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync("/account/sessions/some-client-id");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
