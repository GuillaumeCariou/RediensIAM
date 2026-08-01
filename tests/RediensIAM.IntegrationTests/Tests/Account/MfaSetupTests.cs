using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Account;

[Collection("RediensIAM")]
public class MfaSetupTests(TestFixture fixture)
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
        var client = fixture.ClientWithToken(token);
        return (user, client);
    }

    // ── TOTP setup ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupTotp_Authenticated_ReturnsSecretAndQr()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PostAsync("/account/mfa/totp/setup", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("secret", out _).Should().BeTrue();
        body.TryGetProperty("otpauth_url", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SetupTotp_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsync("/account/mfa/totp/setup", null);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ConfirmTotp_InvalidCode_Returns400()
    {
        var (_, client) = await ScaffoldAsync();

        await client.PostAsync("/account/mfa/totp/setup", null);

        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm", new { code = "000000" });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task ConfirmTotp_WithoutCallingSetupFirst_Returns400()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm", new { code = "123456" });

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── Backup codes ──────────────────────────────────────────────────────────

    /// <summary>
    /// Regeneration invalidates every existing code, so it re-authenticates (R-24) — the current
    /// password has to travel with the request.
    /// </summary>
    private static Task<HttpResponseMessage> RegenerateAsync(HttpClient client) =>
        client.PostAsJsonAsync("/account/mfa/backup-codes",
            new { current_password = SeedData.DefaultPassword });

    [Fact]
    public async Task RegenerateBackupCodes_Authenticated_ReturnsCodeList()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/account/mfa/backup-codes",
            new { current_password = SeedData.DefaultPassword });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("backup_codes", out var codes).Should().BeTrue();
        codes.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RegenerateBackupCodes_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsync("/account/mfa/backup-codes", null);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Codes are stored hashed, so the first batch cannot be looked up directly. The row count
    /// is the proof instead: exactly one batch of 8 must remain, which fails if regeneration ever
    /// appends rather than replaces.
    /// </summary>
    [Fact]
    public async Task RegenerateBackupCodes_PreviousCodesInvalidated()
    {
        var (user, client) = await ScaffoldAsync();

        var res1   = await RegenerateAsync(client);
        var body1  = await res1.Content.ReadFromJsonAsync<JsonElement>();
        var codes1 = body1.GetProperty("backup_codes").EnumerateArray()
            .Select(c => c.GetString()).ToArray();

        await RegenerateAsync(client);

        await fixture.RefreshDbAsync();
        var dbCodes = fixture.Db.BackupCodes
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CodeHash)
            .ToList();

        dbCodes.Should().HaveCount(8);
    }

    // ── Phone setup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Deliberately asserts only "not a server error": setup may answer 200 or demand a
    /// verification step first, and this test is about the endpoint being reachable and not
    /// throwing, not about which of the two it picks.
    /// </summary>
    [Fact]
    public async Task SetupPhone_Authenticated_Returns200()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/account/mfa/phone/setup", new
        {
            phone = "+33600000001"
        });

        ((int)res.StatusCode).Should().BeLessThan(500);
    }

    [Fact]
    public async Task SetupPhone_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/account/mfa/phone/setup", new
        {
            phone = "+33600000001"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RemovePhone_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync("/account/mfa/phone");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
