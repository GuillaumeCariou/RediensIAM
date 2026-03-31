using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

[Collection("RediensIAM")]
public class RegistrationTests(TestFixture fixture)
{
    private static string NewChallenge() => Guid.NewGuid().ToString("N");

    private async Task<(Organisation org, Project project)> ScaffoldAsync(
        bool allowRegistration    = true,
        bool emailVerification    = false)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);

        project.AssignedUserListId      = list.Id;
        project.AllowSelfRegistration   = allowRegistration;
        project.EmailVerificationEnabled = emailVerification;
        await fixture.Db.SaveChangesAsync();

        return (org, project);
    }

    // ── Direct registration (no verification) ────────────────────────────────

    [Fact]
    public async Task Register_ValidRequest_NoVerification_ReturnsRedirectTo()
    {
        var (org, project) = await ScaffoldAsync();
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Register_ValidRequest_CreatesUserInDb()
    {
        var (org, project) = await ScaffoldAsync();
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        var email = SeedData.UniqueEmail();

        await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email,
            password = "P@ssw0rd!Test"
        });

        await fixture.RefreshDbAsync();
        var user = fixture.Db.Users.FirstOrDefault(u => u.Email == email);
        user.Should().NotBeNull();
    }

    // ── Registration disabled ─────────────────────────────────────────────────

    [Fact]
    public async Task Register_RegistrationDisabled_Returns403()
    {
        var (org, project) = await ScaffoldAsync(allowRegistration: false);
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("registration_not_allowed");
    }

    // ── Duplicate email ───────────────────────────────────────────────────────

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var (org, project) = await ScaffoldAsync();
        var email          = SeedData.UniqueEmail();

        // First registration
        var ch1 = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch1, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch1, email, password = "P@ssw0rd!Test"
        });

        // Second with same email
        var ch2 = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(ch2, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = ch2, email, password = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Password policy ───────────────────────────────────────────────────────

    [Fact]
    public async Task Register_PasswordTooShort_Returns400()
    {
        var (org, project) = await ScaffoldAsync();
        project.MinPasswordLength = 12;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "Short1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_too_short");
    }

    [Fact]
    public async Task Register_PasswordRequiresUppercase_Returns400()
    {
        var (org, project) = await ScaffoldAsync();
        project.PasswordRequireUppercase = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "alllowercase1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_uppercase");
    }

    [Fact]
    public async Task Register_PasswordRequiresDigit_Returns400()
    {
        var (org, project) = await ScaffoldAsync();
        project.PasswordRequireDigit = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "NoDigitsHere!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_digit");
    }

    [Fact]
    public async Task Register_PasswordRequiresSpecial_Returns400()
    {
        var (org, project) = await ScaffoldAsync();
        project.PasswordRequireSpecial = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "NoSpecial1234"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_special");
    }

    [Fact]
    public async Task Register_PasswordRequiresLowercase_Returns400()
    {
        var (org, project) = await ScaffoldAsync();
        project.PasswordRequireLowercase = true;
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "ALLUPPERCASE1!"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_requires_lowercase");
    }

    // ── Email domain restriction ──────────────────────────────────────────────

    [Fact]
    public async Task Register_DisallowedDomain_Returns403()
    {
        var (org, project) = await ScaffoldAsync();
        project.AllowedEmailDomains = ["company.com"];
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = "user@gmail.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("domain_not_allowed");
    }

    [Fact]
    public async Task Register_AllowedDomain_Succeeds()
    {
        var (org, project) = await ScaffoldAsync();
        project.AllowedEmailDomains = ["company.com"];
        await fixture.Db.SaveChangesAsync();

        var challenge = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = $"{Guid.NewGuid():N}@company.com",
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Email verification flow ───────────────────────────────────────────────

    [Fact]
    public async Task Register_EmailVerificationEnabled_ReturnsRequiresVerification()
    {
        var (org, project) = await ScaffoldAsync(emailVerification: true);
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        fixture.EmailStub.SentEmails.Clear();

        var res = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("requires_verification").GetBoolean().Should().BeTrue();
        body.TryGetProperty("session_id", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Register_EmailVerification_SendsEmail()
    {
        var (org, project) = await ScaffoldAsync(emailVerification: true);
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        fixture.EmailStub.SentEmails.Clear();
        var email = SeedData.UniqueEmail();

        await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email,
            password = "P@ssw0rd!Test"
        });

        fixture.EmailStub.SentEmails.Should().Contain(e =>
            e.To == email && e.Purpose == "registration");
    }

    [Fact]
    public async Task RegisterVerify_ValidCode_ReturnsRedirectTo()
    {
        var (org, project) = await ScaffoldAsync(emailVerification: true);
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());
        fixture.EmailStub.SentEmails.Clear();

        var registerRes = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });
        var registerBody = await registerRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId    = registerBody.GetProperty("session_id").GetString()!;
        var code         = fixture.EmailStub.SentEmails.Last().Code;

        var res = await fixture.Client.PostAsJsonAsync("/auth/register/verify", new
        {
            session_id = sessionId,
            code
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RegisterVerify_InvalidCode_Returns400()
    {
        var (org, project) = await ScaffoldAsync(emailVerification: true);
        var challenge      = NewChallenge();
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var registerRes = await fixture.Client.PostAsJsonAsync("/auth/register", new
        {
            login_challenge = challenge,
            email           = SeedData.UniqueEmail(),
            password        = "P@ssw0rd!Test"
        });
        var registerBody = await registerRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId    = registerBody.GetProperty("session_id").GetString()!;

        var res = await fixture.Client.PostAsJsonAsync("/auth/register/verify", new
        {
            session_id = sessionId,
            code       = "000000"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [Fact]
    public async Task RegisterVerify_InvalidSessionId_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/register/verify", new
        {
            session_id = "nonexistent-session",
            code       = "123456"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
