using System.Net.Http.Headers;
using Fido2NetLib;
using Fido2NetLib.Objects;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Authentication-path regressions: MFA credential binding, credential-stuffing
/// counters, account-enumeration oracles, and password-policy enforcement.
/// </summary>
[Collection("RediensIAM")]
public class AuthHardeningRegressionTests(TestFixture fixture)
{
    private static string B64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<(Organisation Org, Project Project, UserList List)> CreateTenantAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    // ── REG-SEC-05: WebAuthn assertion not bound to the pending MFA user ─────

    /// <summary>
    /// AuthController.WebAuthnVerify looks the credential up by rawId across the whole
    /// table and only passes ownership to Fido2 through IsUserHandleOwnerOfCredentialId,
    /// which the library skips for non-discoverable credentials (no userHandle in the
    /// response). An attacker who holds any registered authenticator can therefore satisfy
    /// the second factor for a victim whose password they already know.
    ///
    /// Flow: victim reaches the WebAuthn MFA step (password verified, session pending),
    /// then the attacker's own credential is submitted. Must be refused.
    /// </summary>
    [Fact]
    public async Task WebAuthnVerify_CredentialOwnedByAnotherUser_IsRejected()
    {
        var attackerCredId = Guid.NewGuid().ToByteArray();
        var fido2Mock = new AlwaysSucceedingFido2(attackerCredId);
        var (client, factory) = fixture.CreateFido2MockClient(fido2Mock);
        await using var _f = factory;

        var (org, project, list) = await CreateTenantAsync();

        var victim = await fixture.Seed.CreateUserAsync(list.Id);
        victim.WebAuthnEnabled = true;
        fixture.Db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id           = Guid.NewGuid(),
            UserId       = victim.Id,
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey    = new byte[65],
            SignCount    = 0L,
            DeviceName   = "VictimKey",
            CreatedAt    = DateTimeOffset.UtcNow,
        });

        // Attacker's authenticator — registered, valid, but on a different account.
        var attacker = await fixture.Seed.CreateUserAsync(list.Id);
        attacker.WebAuthnEnabled = true;
        fixture.Db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id           = Guid.NewGuid(),
            UserId       = attacker.Id,
            CredentialId = attackerCredId,
            PublicKey    = new byte[65],
            SignCount    = 0L,
            DeviceName   = "AttackerKey",
            CreatedAt    = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        fixture.Keto.AllowAll();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        // Victim's password is correct → session now pending WebAuthn for the victim.
        var loginRes = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = victim.Email,
            password        = "P@ssw0rd!Test",
        });
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        (await loginRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("mfa_type").GetString().Should().Be("webauthn");

        var optRes = await client.GetAsync("/auth/mfa/webauthn/options");
        optRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Submit the ATTACKER's credential against the VICTIM's pending session.
        var res = await client.PostAsJsonAsync("/auth/mfa/webauthn/verify", new
        {
            id    = B64Url(attackerCredId),
            rawId = B64Url(attackerCredId),
            type  = "public-key",
            response = new
            {
                authenticatorData = B64Url(new byte[37]),
                clientDataJSON    = B64Url(new byte[50]),
                signature         = B64Url(new byte[64]),
            },
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the credential lookup must be scoped to the user pending MFA, not global");

        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("redirect_to", "no login may be completed for the victim");
    }

    // ── REG-SEC-06: password-reset account-enumeration oracle ────────────────

    /// <summary>
    /// RequestPasswordReset generates an OTP session either way, but only returns
    /// <c>session_id</c> when the address exists. The response body is the oracle.
    /// Both branches must be byte-identical.
    /// </summary>
    [Fact]
    public async Task PasswordResetRequest_ExistingAndUnknownEmail_ResponsesAreIndistinguishable()
    {
        var (_, project, list) = await CreateTenantAsync();
        project.EmailVerificationEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        await fixture.FlushCacheAsync();

        var existing = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request",
            new { project_id = project.Id, email = user.Email });
        var unknown = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request",
            new { project_id = project.Id, email = $"nobody-{Guid.NewGuid():N}@test.com" });

        existing.StatusCode.Should().Be(unknown.StatusCode);

        var existingKeys = JsonSerializer.Deserialize<JsonElement>(await existing.Content.ReadAsStringAsync())
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        var unknownKeys = JsonSerializer.Deserialize<JsonElement>(await unknown.Content.ReadAsStringAsync())
            .EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();

        unknownKeys.Should().BeEquivalentTo(existingKeys,
            "a differing response shape tells an attacker whether the address is registered");
    }

    // ── REG-SEC-07: per-IP counter cleared by any successful login ───────────

    /// <summary>
    /// A successful login calls ResetAsync(ip, userId), which deletes the shared
    /// <c>rate:login:{ip}</c> key. An attacker holding one valid account can therefore
    /// wipe the per-IP credential-stuffing counter after every few attempts and brute
    /// force other accounts from the same address indefinitely.
    /// Resetting must only clear the per-user counter.
    /// </summary>
    [Fact]
    public async Task RateLimiter_SuccessfulLogin_DoesNotClearSharedIpCounter()
    {
        await fixture.FlushCacheAsync();
        var limiter = fixture.GetService<LoginRateLimiter>();

        var ip       = $"203.0.113.{Random.Shared.Next(2, 250)}";
        var victimId = Guid.NewGuid();
        var ownedId  = Guid.NewGuid();

        // Attacker burns the whole per-IP budget guessing the victim's password.
        for (var i = 0; i < 5; i++)
            await limiter.RecordFailureAsync(ip, victimId);

        (await limiter.IsBlockedAsync(ip)).Should().BeTrue("the IP budget is exhausted");

        // Attacker logs in successfully to an account they legitimately own, same IP.
        await limiter.ResetAsync(ip, ownedId);

        // The per-IP counter must survive. Checked without a userId so the victim's
        // own per-user counter cannot mask the result.
        (await limiter.IsBlockedAsync(ip)).Should().BeTrue(
            "a successful login on an unrelated account must not clear the shared per-IP counter");

        // The attacker's own per-user counter is the only thing that may be cleared.
        (await limiter.IsBlockedAsync("198.51.100.7", ownedId)).Should().BeFalse();
    }

    // ── REG-SEC-09: self-service password change ignores project policy ─────

    /// <summary>
    /// AccountController.ChangePassword hardcodes a minimum of 8 characters and skips
    /// the breach check, so a user can downgrade below the policy their tenant enforces
    /// at registration and at admin-driven creation.
    /// </summary>
    [Fact]
    public async Task ChangePassword_BelowProjectMinimumLength_IsRejected()
    {
        var (org, project, list) = await CreateTenantAsync();
        project.MinPasswordLength        = 16;
        project.PasswordRequireUppercase = true;
        project.PasswordRequireDigit     = true;
        await fixture.Db.SaveChangesAsync();

        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        await fixture.FlushCacheAsync();

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "P@ssw0rd!Test",
            new_password     = "shortpw123",   // 10 chars — below both the absolute floor and the tenant's 16
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "self-service password change must honour the project password policy");

        await fixture.RefreshDbAsync();
        var reloaded = await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id);
        fixture.GetService<PasswordService>()
            .Verify("shortpw123", reloaded.PasswordHash!)
            .Should().BeFalse("the weak password must not have been stored");
    }

    /// <summary>A password satisfying the tenant policy must still be accepted.</summary>
    [Fact]
    public async Task ChangePassword_MeetingProjectPolicy_IsAccepted()
    {
        var (org, project, list) = await CreateTenantAsync();
        project.MinPasswordLength        = 16;
        project.PasswordRequireUppercase = true;
        project.PasswordRequireDigit     = true;
        await fixture.Db.SaveChangesAsync();

        var user   = await fixture.Seed.CreateUserAsync(list.Id);
        var token  = fixture.Seed.UserToken(user.Id, org.Id, project.Id);
        var client = fixture.ClientWithToken(token);
        await fixture.FlushCacheAsync();

        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "P@ssw0rd!Test",
            new_password     = "LongEnoughPassw0rd!",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

// ── Local stub ────────────────────────────────────────────────────────────────

/// <summary>
/// IFido2 whose assertion verification always succeeds — models an attacker holding a
/// genuine, correctly-signing authenticator. Ownership is what is under test, not the
/// cryptography, so the ownership callback is deliberately never consulted (matching
/// the library's behaviour for credentials that carry no userHandle).
/// </summary>
file sealed class AlwaysSucceedingFido2(byte[] assertionCredentialId) : IFido2
{
    private readonly Fido2 _inner = new(new Fido2Configuration
    {
        ServerDomain            = "localhost",
        ServerName              = "RediensIAM-test",
        Origins                 = new HashSet<string> { "http://localhost" },
        TimestampDriftTolerance = 300_000,
    });

    public CredentialCreateOptions RequestNewCredential(RequestNewCredentialParams p)
        => _inner.RequestNewCredential(p);

    public AssertionOptions GetAssertionOptions(GetAssertionOptionsParams p)
        => _inner.GetAssertionOptions(p);

    public Task<RegisteredPublicKeyCredential> MakeNewCredentialAsync(
        MakeNewCredentialParams p, CancellationToken ct = default)
        => Task.FromResult(new RegisteredPublicKeyCredential
        {
            Id        = Guid.NewGuid().ToByteArray(),
            PublicKey = new byte[65],
            SignCount = 0u,
        });

    public Task<VerifyAssertionResult> MakeAssertionAsync(
        MakeAssertionParams p, CancellationToken ct = default)
        => Task.FromResult(new VerifyAssertionResult
        {
            CredentialId = assertionCredentialId,
            SignCount    = p.StoredSignatureCounter + 1u,
        });
}
