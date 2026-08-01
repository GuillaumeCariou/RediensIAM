using System.Net.Http.Headers;
using OtpNet;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Account;

/// <summary>
/// Coverage for phone MFA, TOTP confirm, WebAuthn, and social-account endpoints
/// that were not exercised by the original AccountTests / MfaSetupTests.
/// </summary>
[Collection("RediensIAM")]
public class AccountExtendedTests(TestFixture fixture)
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

    /// <summary>
    /// Removing a live MFA factor re-authenticates (R-24), so every delete in this file has to
    /// carry the current password in the request body.
    /// </summary>
    private static Task<HttpResponseMessage> DeleteWithReauthAsync(HttpClient client, string url) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = JsonContent.Create(new { current_password = SeedData.DefaultPassword }),
        });

    // ── PATCH /account/me — NewDeviceAlertsEnabled branch ────────────────────

    [Fact]
    public async Task UpdateMe_NewDeviceAlertsEnabled_UpdatesFlag()
    {
        var (user, _, client) = await ScaffoldAsync();

        var res = await client.PatchAsJsonAsync("/account/me", new { new_device_alerts_enabled = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.NewDeviceAlertsEnabled.Should().BeFalse();
    }

    // ── TOTP confirm — success path ───────────────────────────────────────────

    [Fact]
    public async Task ConfirmTotp_ValidCode_EnablesTotpAndReturnsBackupCodes()
    {
        var (_, _, client) = await ScaffoldAsync();

        var setupRes  = await client.PostAsync("/account/mfa/totp/setup", null);
        setupRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var setupBody = await setupRes.Content.ReadFromJsonAsync<JsonElement>();
        var base32    = setupBody.GetProperty("secret").GetString()!;

        var secretBytes = Base32Encoding.ToBytes(base32);
        var validCode   = new Totp(secretBytes).ComputeTotp();

        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm", new { code = validCode });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("backup_codes", out var codes).Should().BeTrue();
        codes.GetArrayLength().Should().Be(8);
    }

    // ── Phone MFA — verify ────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyPhone_ValidCode_ReturnsOk()
    {
        var (_, _, client) = await ScaffoldAsync();
        var phone = $"+336{Random.Shared.Next(10000000, 99999999)}";

        var setupRes = await client.PostAsJsonAsync("/account/mfa/phone/setup", new { phone });
        setupRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var code = fixture.SmsStub.SentMessages.Last(s => s.To == phone).Code;

        var res = await client.PostAsJsonAsync("/account/mfa/phone/verify", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("phone_verified");
    }

    [Fact]
    public async Task VerifyPhone_InvalidCode_Returns400()
    {
        var (_, _, client) = await ScaffoldAsync();
        var phone = $"+336{Random.Shared.Next(10000000, 99999999)}";

        await client.PostAsJsonAsync("/account/mfa/phone/setup", new { phone });

        var res = await client.PostAsJsonAsync("/account/mfa/phone/verify", new { code = "000000" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [Fact]
    public async Task VerifyPhone_NoSession_Returns400()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/account/mfa/phone/verify", new { code = "123456" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_setup_session");
    }

    [Fact]
    public async Task VerifyPhone_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/account/mfa/phone/verify", new { code = "123456" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Phone MFA — remove ────────────────────────────────────────────────────

    [Fact]
    public async Task RemovePhone_Authenticated_ClearsPhoneAndReturns200()
    {
        var (user, _, client) = await ScaffoldAsync();

        user.Phone         = "+33600000000";
        user.PhoneVerified = true;
        await fixture.Db.SaveChangesAsync();

        var res = await DeleteWithReauthAsync(client, "/account/mfa/phone");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.Phone.Should().BeNull();
        updated.PhoneVerified.Should().BeFalse();
    }

    [Fact]
    public async Task RemovePhone_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync("/account/mfa/phone");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── WebAuthn — register begin ─────────────────────────────────────────────

    [Fact]
    public async Task WebAuthnRegisterBegin_Authenticated_ReturnsAttestationOptions()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PostAsync("/account/mfa/webauthn/register/begin", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("challenge", out _).Should().BeTrue();
    }

    [Fact]
    public async Task WebAuthnRegisterBegin_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsync("/account/mfa/webauthn/register/begin", null);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── WebAuthn — register complete ─────────────────────────────────────────

    [Fact]
    public async Task WebAuthnRegisterComplete_NoSession_Returns400()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.PostAsJsonAsync("/account/mfa/webauthn/register/complete", new
        {
            response    = new { },
            device_name = "Test Device"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("no_registration_session");
    }

    [Fact]
    public async Task WebAuthnRegisterComplete_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.PostAsJsonAsync("/account/mfa/webauthn/register/complete", new
        {
            response    = new { },
            device_name = "Test Device"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── WebAuthn — list credentials ───────────────────────────────────────────

    [Fact]
    public async Task ListWebAuthnCredentials_Authenticated_ReturnsEmptyList()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/account/mfa/webauthn/credentials");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement[]>();
        body.Should().NotBeNull();
    }

    [Fact]
    public async Task ListWebAuthnCredentials_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/account/mfa/webauthn/credentials");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── WebAuthn — delete credential ──────────────────────────────────────────

    [Fact]
    public async Task DeleteWebAuthnCredential_NotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/account/mfa/webauthn/credentials/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteWebAuthnCredential_LastCredential_ClearsWebAuthnFlagAndReturns200()
    {
        var (user, _, client) = await ScaffoldAsync();

        var cred = new WebAuthnCredential
        {
            Id           = Guid.NewGuid(),
            UserId       = user.Id,
            CredentialId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            PublicKey    = new byte[] { 9, 10, 11, 12 },
            SignCount    = 0,
            CreatedAt    = DateTimeOffset.UtcNow,
        };
        fixture.Db.WebAuthnCredentials.Add(cred);
        user.WebAuthnEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var res = await DeleteWithReauthAsync(client, $"/account/mfa/webauthn/credentials/{cred.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Users.FindAsync(user.Id);
        updated!.WebAuthnEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteWebAuthnCredential_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync($"/account/mfa/webauthn/credentials/{Guid.NewGuid()}");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Social accounts — list ────────────────────────────────────────────────

    [Fact]
    public async Task GetSocialAccounts_Authenticated_ReturnsList()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.GetAsync("/account/social-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement[]>();
        body.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSocialAccounts_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.GetAsync("/account/social-accounts");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Social accounts — unlink ──────────────────────────────────────────────

    [Fact]
    public async Task UnlinkSocialAccount_NotFound_Returns404()
    {
        var (_, _, client) = await ScaffoldAsync();

        var res = await client.DeleteAsync($"/account/social-accounts/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnlinkSocialAccount_LastAuthMethod_Returns400()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        user.PasswordHash = null;
        var social = new UserSocialAccount
        {
            Id             = Guid.NewGuid(),
            UserId         = user.Id,
            Provider       = "github",
            ProviderUserId = Guid.NewGuid().ToString(),
            Email          = user.Email,
            LinkedAt       = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserSocialAccounts.Add(social);
        await fixture.Db.SaveChangesAsync();

        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/account/social-accounts/{social.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("cannot_remove_last_auth_method");
    }

    [Fact]
    public async Task UnlinkSocialAccount_UserHasPassword_Returns204()
    {
        var (user, _, client) = await ScaffoldAsync();

        var social = new UserSocialAccount
        {
            Id             = Guid.NewGuid(),
            UserId         = user.Id,
            Provider       = "github",
            ProviderUserId = Guid.NewGuid().ToString(),
            Email          = user.Email,
            LinkedAt       = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserSocialAccounts.Add(social);
        await fixture.Db.SaveChangesAsync();

        var res = await client.DeleteAsync($"/account/social-accounts/{social.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task UnlinkSocialAccount_Unauthenticated_Returns401()
    {
        var res = await fixture.Client.DeleteAsync($"/account/social-accounts/{Guid.NewGuid()}");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── WebAuthn — register complete — attestation_failed path (lines 257-273) ─

    [Fact]
    public async Task WebAuthnRegisterComplete_InvalidAttestation_Returns400AttestationFailed()
    {
        var (_, _, client) = await ScaffoldAsync();

        var beginRes = await client.PostAsync("/account/mfa/webauthn/register/begin", null);
        beginRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await client.PostAsJsonAsync("/account/mfa/webauthn/register/complete", new
        {
            response    = new { clientDataJSON = "INVALID_BASE64", attestationObject = "INVALID_BASE64" },
            device_name = "My Key"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("attestation_failed");
    }
}
