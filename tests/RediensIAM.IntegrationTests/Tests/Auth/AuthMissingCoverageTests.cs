using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Covers AuthController lines not yet hit by existing test files:
///   - AdminLogin lockout after MaxLoginAttempts failures (line 913)
///   - VerifyRegistration duplicate email (line 730)
/// </summary>
[Collection("RediensIAM")]
public class AuthMissingCoverageTests(TestFixture fixture)
{
    // ── AdminLogin — lockout after MaxLoginAttempts (line 913) ───────────────

    /// <summary>
    /// After 5 failed admin login attempts (MaxLoginAttempts=5),
    /// user.LockedUntil is set — covers line 913.
    /// </summary>
    [Fact]
    public async Task AdminLogin_MaxFailedAttempts_SetsLockedUntil()
    {
        // Create a system-level user (OrgId=null, Immovable=true) for admin login
        var list = new UserList
        {
            Id        = Guid.NewGuid(),
            Name      = $"sys-lock-{Guid.NewGuid():N}"[..20],
            OrgId     = null,
            Immovable = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(list);
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id, password: "Correct@Pass123!");

        // Pre-set FailedLoginCount to MaxLoginAttempts-1 so a single wrong-password call triggers lockout
        user.FailedLoginCount = 4;  // MaxLoginAttempts=5; one more → LockedUntil set (line 913)
        await fixture.Db.SaveChangesAsync();

        fixture.Keto.AllowAll();

        var ch = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(ch, "client_admin_system");
        await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = ch,
            email           = user.Email,
            password        = "WRONG_PASSWORD"
        });

        // Verify LockedUntil was set on the user (line 913 executed)
        await fixture.RefreshDbAsync();
        var reloaded = await fixture.Db.Users.FindAsync(user.Id);
        reloaded!.LockedUntil.Should().NotBeNull();
        reloaded.LockedUntil.Should().BeAfter(DateTimeOffset.UtcNow);

        // Reset IP rate counter so subsequent tests are not blocked (only 1 failure added)
        await fixture.FlushCacheAsync();
    }

    // ── VerifyRegistration — duplicate email race condition (line 730) ────────

    /// <summary>
    /// When registration with email verification is in flight and another user
    /// registers the same email, VerifyRegistration returns 409 — covers line 730.
    /// </summary>
    [Fact]
    public async Task VerifyRegistration_DuplicateEmailRace_Returns409()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.AllowSelfRegistration    = true;
        project.EmailVerificationEnabled = true;
        await fixture.Db.SaveChangesAsync();

        var email     = SeedData.UniqueEmail();
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        fixture.EmailStub.SentEmails.Clear();

        // Step 1: start registration — creates a pending OTP session
        var regRes = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email,
            password = "P@ssw0rd!Test"
        });
        regRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var regBody   = await regRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = regBody.GetProperty("session_id").GetString()!;

        // The stub email service captures the OTP code
        var sent = fixture.EmailStub.SentEmails.LastOrDefault(e => e.To == email && e.Purpose == "registration");
        sent.Should().NotBeNull("email stub should capture the OTP");
        var otp = sent!.Code;

        // Step 2: seed a user with the same email directly — simulates race condition
        await fixture.Seed.CreateUserAsync(list.Id, email: email);

        // Step 3: verify registration — hits line 730 (email_already_exists)
        var verifyRes = await fixture.Client.PostAsJsonAsync("/auth/register/verify", new
        {
            session_id = sessionId,
            code       = otp
        });

        verifyRes.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await verifyRes.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("email_already_exists");
    }
}
