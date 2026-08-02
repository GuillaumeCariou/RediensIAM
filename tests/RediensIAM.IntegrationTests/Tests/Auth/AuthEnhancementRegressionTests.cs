using OtpNet;
using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Step 8 — authentication and authorisation enhancement.
///
/// Step 4 closed factor *replacement* and *removal*. Four gaps were left:
///
///  1. Adding a factor was unguarded. A stolen token could enrol the attacker's own passkey or
///     phone number beside the victim's factor. That grants MFA on every future login and, unlike
///     a stolen password, survives <c>ChangePassword</c> — which revokes sessions but never
///     touches enrolled factors.
///  2. WebAuthn registration asked for <c>UserVerification = Preferred</c> while the assertion
///     demands <c>Required</c>, so a credential could be enrolled that is either unusable or not
///     actually a verified factor.
///  3. The management console had no MFA policy. A tenant project has <c>RequireMfa</c>;
///     RediensIAM's own <c>super_admin</c> surface asked for a second factor only when the account
///     happened to have one.
///  4. Revoking a management role or suspending an organisation dropped the live-authorisation
///     cache but left every issued token alive. A resource server validating the JWT locally
///     honoured the revoked role for the token's full lifetime (A / R-22).
/// </summary>
[Collection("RediensIAM")]
public class AuthEnhancementRegressionTests(TestFixture fixture)
{
    private const string Password = SeedData.DefaultPassword;

    private async Task<(User User, HttpClient Client)> ScaffoldAsync(bool withFactor)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id, password: Password);
        if (withFactor)
        {
            var appConfig = fixture.GetService<AppConfig>();
            user.TotpSecret  = TotpEncryption.Encrypt(appConfig.TotpEncKey, KeyGeneration.GenerateRandomKey(20));
            user.TotpEnabled = true;
            await fixture.Db.SaveChangesAsync();
        }

        return (user, fixture.ClientWithToken(fixture.Seed.UserToken(user.Id, org.Id, project.Id)));
    }

    // ── 1. Adding a factor to an account that already has one ────────────────

    [Fact]
    public async Task PhoneVerify_OnAnAccountWithAFactor_WithoutReauth_IsRefusedAndNotPersisted()
    {
        await fixture.FlushCacheAsync();
        var (user, client) = await ScaffoldAsync(withFactor: true);

        (await client.PostAsJsonAsync("/account/mfa/phone/setup", new { phone = "+33600000001" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var code = fixture.SmsStub.SentMessages.Last().Code;

        var res = await client.PostAsJsonAsync("/account/mfa/phone/verify", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().Contain("reauthentication_required");

        fixture.Db.ChangeTracker.Clear();
        var reloaded = await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        reloaded.PhoneVerified.Should().BeFalse(
            "a stolen token must not be able to enrol the attacker's phone as a second factor");
        reloaded.Phone.Should().BeNull();
    }

    [Fact]
    public async Task PhoneVerify_OnAnAccountWithAFactor_WithCurrentPassword_Succeeds()
    {
        await fixture.FlushCacheAsync();
        var (user, client) = await ScaffoldAsync(withFactor: true);

        await client.PostAsJsonAsync("/account/mfa/phone/setup", new { phone = "+33600000002" });
        var code = fixture.SmsStub.SentMessages.Last().Code;

        var res = await client.PostAsJsonAsync("/account/mfa/phone/verify",
            new { code, reauth = new { current_password = Password } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id))
            .PhoneVerified.Should().BeTrue();
    }

    [Fact]
    public async Task PhoneVerify_AsTheFirstFactor_StillNeedsNoProof()
    {
        await fixture.FlushCacheAsync();
        var (user, client) = await ScaffoldAsync(withFactor: false);

        await client.PostAsJsonAsync("/account/mfa/phone/setup", new { phone = "+33600000003" });
        var code = fixture.SmsStub.SentMessages.Last().Code;

        var res = await client.PostAsJsonAsync("/account/mfa/phone/verify", new { code });

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "first enrolment is not a takeover — there is nothing to re-authenticate against");
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id))
            .PhoneVerified.Should().BeTrue();
    }

    [Fact]
    public async Task PasskeyRegistration_OnAnAccountWithAFactor_IsRefusedBeforeTheAttestationIsRead()
    {
        await fixture.FlushCacheAsync();
        var (_, client) = await ScaffoldAsync(withFactor: true);

        (await client.PostAsync("/account/mfa/webauthn/register/begin", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // A deliberately invalid attestation. Without the guard this reaches Fido2 and answers
        // 400 attestation_failed; the 401 proves the proof is demanded first.
        var body = new { response = new { id = "x", rawId = "eA", type = "public-key" }, device_name = "attacker" };
        var res  = await client.PostAsJsonAsync("/account/mfa/webauthn/register/complete", body);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().Contain("reauthentication_required");

        // The pending registration must survive a refusal, otherwise a legitimate user has to
        // re-tap their authenticator after every prompt. With the proof supplied the request now
        // gets as far as the attestation and fails there instead.
        var retry = await client.PostAsJsonAsync("/account/mfa/webauthn/register/complete",
            new { response = body.response, device_name = "owner", reauth = new { current_password = Password } });
        retry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await retry.Content.ReadAsStringAsync()).Should().NotContain("no_registration_session");
    }

    // ── 2. WebAuthn registration must demand user verification ───────────────

    [Fact]
    public async Task PasskeyRegistrationOptions_DemandUserVerification()
    {
        await fixture.FlushCacheAsync();
        var (_, client) = await ScaffoldAsync(withFactor: false);

        var res = await client.PostAsync("/account/mfa/webauthn/register/begin", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        // Naming-policy agnostic: the app sets snake_case globally while Fido2NetLib's own
        // [JsonPropertyName] attributes emit camelCase, and the assertion must not depend on which
        // one wins.
        var normalised = (await res.Content.ReadAsStringAsync())
            .Replace("_", "").Replace("\"", "").ToLowerInvariant();
        normalised.Should().Contain("userverification:required",
            "the assertion path demands Required, so a credential registered under Preferred is "
            + "either unusable or not a verified factor");
    }

    // ── 3. The management console can require a second factor ────────────────

    [Fact]
    public async Task AdminLogin_WithNoFactor_SendsTheAdminThroughEnrolment()
    {
        await fixture.FlushCacheAsync();
        var list = await CreateSystemListAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id, password: Password);
        // Enrolment is mandatory because another administrator of this deployment already holds a
        // factor — it used to be because Security:RequireAdminMfa was on in the fixture. The flag
        // is gone: the only account that may sign in without one is the first, and that case is
        // covered in Tests/Auth/AdminMfaBootstrapTests.cs.
        var enrolled = await fixture.Seed.CreateUserAsync(list.Id, password: Password);
        enrolled.TotpEnabled = true;
        await fixture.Db.SaveChangesAsync();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, Roles.AdminClientId);
        fixture.Keto.AllowAll();

        var res = await fixture.NewSessionClient().PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge, email = user.Email, password = Password,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_mfa_setup").GetBoolean().Should().BeTrue(
            "once any administrator has a factor, RediensIAM's own super_admin surface must not "
            + "be reachable on a password alone");
        body.TryGetProperty("redirect_to", out _).Should().BeFalse();
        fixture.Hydra.LoginWasAccepted(challenge).Should().BeFalse(
            "the login must not complete before a factor exists");
    }

    // ── 4. Revocation reaches the tokens, not just the cache ─────────────────

    [Fact]
    public async Task RemovingAManagementRole_RevokesTheTargetsConsoleSessions()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var actor    = await fixture.Seed.CreateUserAsync(list.Id);
        var target   = await fixture.Seed.CreateUserAsync(list.Id);
        var role     = await fixture.Seed.CreateOrgRoleAsync(org.Id, target.Id, Roles.OrgAdmin);
        fixture.Keto.AllowAll();
        fixture.Hydra.ResetLog();

        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        var res = await client.DeleteAsync($"/admin/organizations/{org.Id}/admins/{role.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fixture.Hydra.SessionsRevokedFor(target.Id.ToString()).Should().BeTrue(
            "the demoted admin's token still asserts org_admin in ext.roles; only revoking the "
            + "session forces one minted from the new grants");
    }

    [Fact]
    public async Task SuspendingAnOrganisation_RevokesEverySessionInIt()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var member   = await fixture.Seed.CreateUserAsync(list.Id);
        var actor    = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        fixture.Hydra.ResetLog();

        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        var res = await client.PostAsync($"/admin/organizations/{org.Id}/suspend", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Hydra.SessionsRevokedFor($"{org.Id}:{member.Id}").Should().BeTrue(
            "Active is only consulted at login, so without revocation a suspended tenant keeps "
            + "full API access for the lifetime of every token already issued");
    }

    [Fact]
    public async Task DeactivatingAUser_RevokesTheirSessions()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var target   = await fixture.Seed.CreateUserAsync(list.Id);
        var actor    = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        fixture.Hydra.ResetLog();

        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        var res = await client.PatchAsJsonAsync($"/admin/users/{target.Id}", new { active = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Hydra.SessionsRevokedFor($"{org.Id}:{target.Id}").Should().BeTrue();
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == "user.deactivated" && a.TargetId == target.Id.ToString()))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ReactivatingAUser_DoesNotRevokeSessions()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var target   = await fixture.Seed.CreateUserAsync(list.Id, active: false);
        var actor    = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        fixture.Hydra.ResetLog();

        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        var res = await client.PatchAsJsonAsync($"/admin/users/{target.Id}", new { active = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Hydra.SessionsRevokedFor($"{org.Id}:{target.Id}").Should().BeFalse(
            "only a deactivation invalidates live sessions");
    }

    // ── Keto / Postgres divergence on the management-grant paths ─────────────

    [Fact]
    public async Task AssigningSuperAdminAsAnOrgRole_IsRefused()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var target   = await fixture.Seed.CreateUserAsync(list.Id);
        var actor    = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();

        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
        {
            user_id = target.Id, role = Roles.SuperAdmin,
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("cannot_grant_super_admin");
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.OrgRoles.AsNoTracking()
            .AnyAsync(r => r.UserId == target.Id && r.Role == Roles.SuperAdmin))
            .Should().BeFalse("an org-scoped super_admin row resolves to nothing and reads as a grant");
    }

    [Fact]
    public async Task AssigningAnOrgRole_WhenKetoRefusesTheTuple_LeavesNoGrantBehind()
    {
        await fixture.FlushCacheAsync();
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var target   = await fixture.Seed.CreateUserAsync(list.Id);
        var actor    = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));
        fixture.Keto.FailTupleWrites();
        try
        {
            var res = await client.PostAsJsonAsync($"/admin/organizations/{org.Id}/admins", new
            {
                user_id = target.Id, role = Roles.OrgAdmin,
            });

            res.IsSuccessStatusCode.Should().BeFalse();
            fixture.Db.ChangeTracker.Clear();
            (await fixture.Db.OrgRoles.AsNoTracking().AnyAsync(r => r.UserId == target.Id))
                .Should().BeFalse("a grant the permission graph never accepted must not be recorded as one");
        }
        finally
        {
            fixture.Keto.AllowAll();
        }
    }

    private async Task<UserList> CreateSystemListAsync()
    {
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-{Guid.NewGuid():N}",
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();
        return list;
    }
}
