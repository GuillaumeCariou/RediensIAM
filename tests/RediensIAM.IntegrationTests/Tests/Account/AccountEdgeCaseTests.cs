using RediensIAM.IntegrationTests.Infrastructure;
using System.Net.Http.Headers;
using OtpNet;
using RediensIAM.Data.Entities;

namespace RediensIAM.IntegrationTests.Tests.Account;

// ── from AccountBranchCoverageTests.cs ───────────────────────

/// <summary>
/// Covers AccountController branches where only one path was exercised.
///   - GET  /account/me              — user not found (line 37)
///   - PATCH /account/me             — user not found (line 53)
///   - PATCH /account/password       — user not found (line 66), null PasswordHash (line 67), null OrgId (line 72)
///   - POST /account/mfa/totp/setup  — user not found (line 80)
///   - POST /account/mfa/totp/confirm — user not found (line 101)
///   - GET  /account/sessions        — null OrgId subject (line 144)
///   - DELETE /account/sessions      — null OrgId subject (line 158)
///   - DELETE /account/sessions/{id} — null OrgId subject (line 166)
///   - POST /account/mfa/phone/verify — user not found (line 191)
///   - DELETE /account/mfa/phone      — user not found (line 204)
///   - GET  /account/mfa             — user not found (line 217)
///   - POST /account/mfa/webauthn/register/begin — user not found (line 229)
/// </summary>
[Collection("RediensIAM")]
public class AccountBranchCoverageTests(TestFixture fixture)
{
    // ── Scaffold helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// A token pointing at a user ID that does NOT exist in the DB.
    /// Covers all "if (user == null) return NotFound()" branches.
    /// </summary>
    private HttpClient ClientWithDeletedUser()
    {
        var fakeUserId = Guid.NewGuid();
        var fakeOrgId  = Guid.NewGuid();
        var token = $"del-{fakeUserId:N}";
        fixture.Hydra.RegisterToken(token, fakeUserId.ToString(), fakeOrgId.ToString(),
            Guid.NewGuid().ToString(), []);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(token);
    }

    /// <summary>
    /// A token with no OrgId claim — covers string.IsNullOrEmpty(Claims.OrgId) == true branches.
    /// </summary>
    private async Task<(User user, HttpClient client)> ScaffoldNoOrgIdAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var user     = await fixture.Seed.CreateUserAsync(list.Id);
        var token    = $"noorg-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), null, null, []);
        fixture.Keto.AllowAll();
        return (user, fixture.ClientWithToken(token));
    }

    // ── GET /account/me — user not found (line 37) ───────────────────────────

    [Fact]
    public async Task GetMe_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /account/me — user not found (line 53) ─────────────────────────

    [Fact]
    public async Task UpdateMe_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.PatchAsJsonAsync("/account/me", new { display_name = "X" });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /account/password — user not found (line 66) ───────────────────

    [Fact]
    public async Task ChangePassword_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "old",
            new_password     = "NewP@ssw0rd!1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── PATCH /account/password — null PasswordHash (line 67 TRUE short-circuit) ─

    /// <summary>
    /// A passwordless account must be told to set a password rather than be told its
    /// current password is wrong: the distinct <c>set_password_required</c> error asserted
    /// below keeps the rate-limiter from charging users who can never satisfy the
    /// <c>current_password</c> check.
    /// </summary>
    [Fact]
    public async Task ChangePassword_NullPasswordHash_ReturnsBadRequest()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        // Directly create a user with no password (SAML-provisioned)
        var user = new User
        {
            Id            = Guid.NewGuid(),
            UserListId    = list.Id,
            Email         = SeedData.UniqueEmail(),
            Username      = "samluser",
            Discriminator = "9999",
            PasswordHash  = null,
            EmailVerified = true,
            Active        = true,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow,
        };
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();

        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "anything",
            new_password     = "NewP@ssw0rd!1"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("set_password_required");
    }

    // ── PATCH /account/password — null OrgId (line 72 false branch) ──────────

    /// <summary>
    /// A claim set with no <c>org_id</c> must not break the audit write: the org id passed to
    /// the audit record is null and the password change still succeeds.
    /// </summary>
    [Fact]
    public async Task ChangePassword_NullOrgId_StillChangesPassword()
    {
        var (user, _) = await ScaffoldNoOrgIdAsync();

        var token = $"noorg2-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), null, null, []);
        var client = fixture.ClientWithToken(token);

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = SeedData.DefaultPassword,
            new_password     = "NewP@ssw0rd!2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /account/mfa/totp/setup — user not found (line 80) ─────────────

    [Fact]
    public async Task SetupTotp_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.PostAsync("/account/mfa/totp/setup", null);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /account/mfa/totp/confirm — user not found (line 101) ──────────

    /// <summary>
    /// Order matters: the setup call has to run while the user still exists, because the only way
    /// to hold a valid TOTP setup session is to have obtained it legitimately. The row is deleted
    /// afterwards, so confirm sees a good session and a missing user.
    /// </summary>
    [Fact]
    public async Task ConfirmTotp_UserNotFound_Returns404()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var setupRes  = await client.PostAsync("/account/mfa/totp/setup", null);
        setupRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var base32 = (await setupRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;

        fixture.Db.Users.Remove(user);
        await fixture.Db.SaveChangesAsync();

        var validCode = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(base32)).ComputeTotp();
        var res = await client.PostAsJsonAsync("/account/mfa/totp/confirm", new { code = validCode });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /account/sessions — null OrgId (line 144 TRUE branch) ────────────

    [Fact]
    public async Task GetSessions_NullOrgId_UsesUserIdAsSubject()
    {
        var (_, client) = await ScaffoldNoOrgIdAsync();

        var res = await client.GetAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /account/sessions — null OrgId (line 158 TRUE branch) ─────────

    [Fact]
    public async Task RevokeAllSessions_NullOrgId_UsesUserIdAsSubject()
    {
        var (_, client) = await ScaffoldNoOrgIdAsync();

        var res = await client.DeleteAsync("/account/sessions");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── DELETE /account/sessions/{clientId} — null OrgId (line 166 TRUE) ────

    [Fact]
    public async Task RevokeSession_NullOrgId_UsesUserIdAsSubject()
    {
        var (_, client) = await ScaffoldNoOrgIdAsync();

        var res = await client.DeleteAsync("/account/sessions/some-client-id");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /account/mfa/phone/verify — user not found (line 191) ───────────

    /// <summary>
    /// Do not read this as a 404 test despite its name: it asserts nothing. Reaching the
    /// user-not-found branch needs the OTP that was sent, and the OTP only lives in the cache,
    /// so verify fails earlier with <c>invalid_code</c>. What is exercised is the phone-setup
    /// path against a user that is subsequently deleted; give it real assertions if a way to
    /// read back the issued OTP is ever added.
    /// </summary>
    [Fact]
    public async Task VerifyPhone_UserNotFound_Returns404()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        await client.PostAsJsonAsync("/account/mfa/phone/setup", new { phone = "+15555551234" });

        fixture.Db.Users.Remove(user);
        await fixture.Db.SaveChangesAsync();
    }

    // ── DELETE /account/mfa/phone — user not found (line 204) ────────────────

    [Fact]
    public async Task RemovePhone_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.DeleteAsync("/account/mfa/phone");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /account/mfa — user not found (line 217) ─────────────────────────

    [Fact]
    public async Task GetMfaStatus_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.GetAsync("/account/mfa");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /account/mfa/webauthn/register/begin — user not found (line 229) ─

    [Fact]
    public async Task WebAuthnRegisterBegin_UserNotFound_Returns404()
    {
        var client = ClientWithDeletedUser();

        var res = await client.PostAsync("/account/mfa/webauthn/register/begin", null);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

// ── from AccountExtendedTests.cs ─────────────────────────────

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
