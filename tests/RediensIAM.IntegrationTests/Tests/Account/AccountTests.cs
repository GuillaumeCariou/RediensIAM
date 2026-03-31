using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Account;

[Collection("RediensIAM")]
public class AccountTests(TestFixture fixture)
{
    private async Task<(User user, string token, HttpClient client)> ScaffoldAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        return (user, token, client);
    }

    // ── GET /account/me ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetMe_Authenticated_ReturnsUserProfile()
    {
        var (user, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("email").GetString().Should().Be(user.Email);
    }

    [Fact]
    public async Task GetMe_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /account/me ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMe_ValidUsername_UpdatesUser()
    {
        var (user, _, client) = await ScaffoldAsync();
        var newDisplayName    = $"newname_{Guid.NewGuid().ToString("N")[..6]}";

        // UpdateMeRequest only supports display_name (not username)
        var res = await client.PatchAsJsonAsync("/account/me", new { display_name = newDisplayName });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.DisplayName.Should().Be(newDisplayName);
    }

    [Fact]
    public async Task UpdateMe_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PatchAsJsonAsync("/account/me", new { username = "hacker" });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── PATCH /account/password ───────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_ValidCurrentPassword_Returns200()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "P@ssw0rd!Test",
            new_password     = "NewP@ssw0rd!2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns400Or401()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "WrongPassword!",
            new_password     = "NewP@ssw0rd!2"
        });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task ChangePassword_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "any",
            new_password     = "any"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /account/mfa ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMfaStatus_Authenticated_ReturnsMfaFlags()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/account/mfa");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("totp_enabled", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetMfaStatus_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/account/mfa");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /account/sessions ─────────────────────────────────────────────────

    [Fact]
    public async Task GetSessions_Authenticated_ReturnsList()
    {
        var (user, _, client) = await ScaffoldAsync();
        fixture.Hydra.SetupConsentSessions(user.Id.ToString(), []);

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
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokeAllSessions_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
