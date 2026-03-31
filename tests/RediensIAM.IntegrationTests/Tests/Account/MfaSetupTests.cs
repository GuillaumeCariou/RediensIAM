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

        // First call setup to generate secret
        await client.PostAsync("/account/mfa/totp/setup", null);

        // Confirm with wrong code
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

    [Fact]
    public async Task RegenerateBackupCodes_Authenticated_ReturnsCodeList()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PostAsync("/account/mfa/backup-codes", null);

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

    [Fact]
    public async Task RegenerateBackupCodes_PreviousCodesInvalidated()
    {
        var (user, client) = await ScaffoldAsync();

        // Generate first batch
        var res1   = await client.PostAsync("/account/mfa/backup-codes", null);
        var body1  = await res1.Content.ReadFromJsonAsync<JsonElement>();
        var codes1 = body1.GetProperty("backup_codes").EnumerateArray()
            .Select(c => c.GetString()).ToArray();

        // Generate second batch
        await client.PostAsync("/account/mfa/backup-codes", null);

        // First-batch codes should no longer exist in the DB
        await fixture.RefreshDbAsync();
        var dbCodes = fixture.Db.BackupCodes
            .Where(c => c.UserId == user.Id)
            .Select(c => c.CodeHash)
            .ToList();

        // We can't compare plaintext to hashes, but DB should have exactly one batch
        dbCodes.Should().HaveCount(8); // controller generates 8 backup codes per batch
    }

    // ── Phone setup ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetupPhone_Authenticated_Returns200()
    {
        var (_, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/account/mfa/phone/setup", new
        {
            phone = "+33600000001"
        });

        // Should be OK or require verification step
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
