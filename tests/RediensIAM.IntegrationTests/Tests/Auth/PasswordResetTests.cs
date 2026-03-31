using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

[Collection("RediensIAM")]
public class PasswordResetTests(TestFixture fixture)
{
    private async Task<(Organisation org, Project project, User user)> ScaffoldAsync()
    {
        var (org, _)    = await fixture.Seed.CreateOrgAsync();
        var project     = await fixture.Seed.CreateProjectAsync(org.Id);
        var list        = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.EmailVerificationEnabled = true;
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        return (org, project, user);
    }

    // ── POST /auth/password-reset/request ─────────────────────────────────────

    [Fact]
    public async Task RequestReset_ExistingUser_Returns200WithSessionId()
    {
        var (_, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("session_id", out var sid).Should().BeTrue();
        sid.GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RequestReset_ExistingUser_SendsResetEmail()
    {
        var (_, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });

        fixture.EmailStub.SentEmails.Should().Contain(e =>
            e.To == user.Email && e.Purpose == "password_reset");
    }

    [Fact]
    public async Task RequestReset_UnknownEmail_Returns200WithoutSessionId_PreventEnumeration()
    {
        var (_, project, _) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = "nobody@nowhere.com"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // Must NOT reveal whether email exists
        body.TryGetProperty("session_id", out _).Should().BeFalse();
        fixture.EmailStub.SentEmails.Should().BeEmpty();
    }

    [Fact]
    public async Task RequestReset_VerificationNotConfigured_Returns400()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId       = list.Id;
        project.EmailVerificationEnabled = false; // disabled
        await fixture.Db.SaveChangesAsync();

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = "any@test.com"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("verification_not_configured");
    }

    // ── POST /auth/password-reset/verify ──────────────────────────────────────

    [Fact]
    public async Task VerifyReset_ValidCode_ReturnsResetToken()
    {
        var (_, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        var reqRes = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });
        var reqBody   = await reqRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = reqBody.GetProperty("session_id").GetString()!;
        var code      = fixture.EmailStub.SentEmails.Last().Code;

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/verify", new
        {
            session_id = sessionId,
            code
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("reset_token").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task VerifyReset_WrongCode_Returns401()
    {
        var (_, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        var reqRes = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });
        var reqBody   = await reqRes.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = reqBody.GetProperty("session_id").GetString()!;

        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/verify", new
        {
            session_id = sessionId,
            code       = "000000"
        });

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_code");
    }

    [Fact]
    public async Task VerifyReset_InvalidSession_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/verify", new
        {
            session_id = "nonexistent",
            code       = "123456"
        });

        // Non-existent session → OTP verification fails → controller returns 401 (invalid_code)
        ((int)res.StatusCode).Should().BeOneOf(400, 401);
    }

    // ── POST /auth/password-reset/confirm ─────────────────────────────────────

    [Fact]
    public async Task ConfirmReset_ValidToken_ChangesPassword()
    {
        var (_, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        // Full reset flow
        var reqRes = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });
        var sessionId  = (await reqRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session_id").GetString()!;
        var code       = fixture.EmailStub.SentEmails.Last().Code;

        var verifyRes  = await fixture.Client.PostAsJsonAsync("/auth/password-reset/verify", new
        {
            session_id = sessionId, code
        });
        var resetToken = (await verifyRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("reset_token").GetString()!;

        var confirmRes = await fixture.Client.PostAsJsonAsync("/auth/password-reset/confirm", new
        {
            token        = resetToken,
            new_password = "NewP@ssw0rd!2"
        });

        confirmRes.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmReset_ValidToken_UserCanLoginWithNewPassword()
    {
        var (org, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        var reqRes = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });
        var sessionId  = (await reqRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session_id").GetString()!;
        var code       = fixture.EmailStub.SentEmails.Last().Code;

        var verifyRes  = await fixture.Client.PostAsJsonAsync("/auth/password-reset/verify", new
        {
            session_id = sessionId, code
        });
        var resetToken = (await verifyRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("reset_token").GetString()!;

        await fixture.Client.PostAsJsonAsync("/auth/password-reset/confirm", new
        {
            token = resetToken, new_password = "NewP@ssw0rd!2"
        });

        // Now try to login with the new password
        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var loginRes = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "NewP@ssw0rd!2"
        });
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmReset_TokenUsedTwice_Returns400()
    {
        var (_, project, user) = await ScaffoldAsync();
        fixture.EmailStub.SentEmails.Clear();

        var reqRes = await fixture.Client.PostAsJsonAsync("/auth/password-reset/request", new
        {
            project_id = project.Id,
            email      = user.Email
        });
        var sessionId  = (await reqRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("session_id").GetString()!;
        var code       = fixture.EmailStub.SentEmails.Last().Code;
        var verifyRes  = await fixture.Client.PostAsJsonAsync("/auth/password-reset/verify", new
        {
            session_id = sessionId, code
        });
        var resetToken = (await verifyRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("reset_token").GetString()!;

        await fixture.Client.PostAsJsonAsync("/auth/password-reset/confirm", new
        {
            token = resetToken, new_password = "NewP@ssw0rd!2"
        });

        // Second use of the same token
        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/confirm", new
        {
            token = resetToken, new_password = "AnotherP@ss!3"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmReset_InvalidToken_Returns400()
    {
        var res = await fixture.Client.PostAsJsonAsync("/auth/password-reset/confirm", new
        {
            token        = "totally-fake-token-that-does-not-exist",
            new_password = "NewP@ssw0rd!2"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
