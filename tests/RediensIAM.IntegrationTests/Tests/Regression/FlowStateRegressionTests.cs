using System.Net.Http.Headers;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Flows that were broken by keeping state in the ASP.NET session cookie, and by offering
/// factors no configured provider can deliver.
///
/// The cookie is <c>SameSite=Strict</c>, so it is absent on a cross-site POST (the SAML ACS)
/// and on requests from an admin console served from a different origin than the API — the
/// documented NodePort / Tailscale / private-ingress layout. Neither situation is reproducible
/// through TestServer, which has no cross-site notion, so these tests assert the property that
/// actually matters: the flow completes with **no session cookie at all**.
/// </summary>
[Collection("RediensIAM")]
public class FlowStateRegressionTests(TestFixture fixture)
{
    private async Task<(Organisation Org, Project Project, UserList List)> CreateTenantAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        return (org, project, list);
    }

    /// <summary>A client that never returns a cookie — models a cross-origin caller.</summary>
    private HttpClient CookielessClient(string token)
    {
        var client = fixture.NewSessionClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // ── REG-FUNC-07: TOTP enrolment must not depend on a cookie ──────────────

    [Fact]
    public async Task TotpEnrolment_WithoutSessionCookie_Completes()
    {
        var (org, project, list) = await CreateTenantAsync();
        var user  = await fixture.Seed.CreateUserAsync(list.Id);
        var token = fixture.Seed.UserToken(user.Id, org.Id, project.Id);

        // Deliberately separate clients: the confirm call shares no cookie jar with setup.
        var setupRes = await CookielessClient(token).PostAsync("/account/mfa/totp/setup", null);
        setupRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = (await setupRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secret").GetString()!;

        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();

        var confirmRes = await CookielessClient(token)
            .PostAsJsonAsync("/account/mfa/totp/confirm", new { code });

        confirmRes.StatusCode.Should().Be(HttpStatusCode.OK,
            "enrolment state must live server-side, not in a SameSite=Strict cookie");

        await fixture.RefreshDbAsync();
        (await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id))
            .TotpEnabled.Should().BeTrue();
    }

    /// <summary>A wrong code must let the user retry, not discard the secret they just scanned.</summary>
    [Fact]
    public async Task TotpEnrolment_WrongCode_KeepsPendingSecret()
    {
        var (org, project, list) = await CreateTenantAsync();
        var user  = await fixture.Seed.CreateUserAsync(list.Id);
        var token = fixture.Seed.UserToken(user.Id, org.Id, project.Id);

        var setupRes = await CookielessClient(token).PostAsync("/account/mfa/totp/setup", null);
        var secret = (await setupRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secret").GetString()!;

        var wrong = await CookielessClient(token)
            .PostAsJsonAsync("/account/mfa/totp/confirm", new { code = "000000" });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("invalid_code");

        // Same secret still works — no need to restart enrolment.
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        var retry = await CookielessClient(token)
            .PostAsJsonAsync("/account/mfa/totp/confirm", new { code });

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── REG-FUNC-06: SAML request state must not depend on a cookie ──────────

    /// <summary>
    /// The pending AuthnRequest is keyed in Redis by its own ID, which the IdP echoes back in
    /// InResponseTo. Starting the flow on one client and consuming it from another (no shared
    /// cookie jar, as with the IdP's cross-site POST) must still resolve.
    /// </summary>
    [Fact]
    public async Task SamlStart_StoresPendingRequestServerSide()
    {
        var (org, project, _) = await CreateTenantAsync();

        var idp = new SamlIdpConfig
        {
            Id                 = Guid.NewGuid(),
            ProjectId          = project.Id,
            EntityId           = "https://idp.example.com",
            SsoUrl             = "https://idp.example.com/sso",
            CertificatePem     = Tests.Auth.SamlControllerTests.TestCertPem,
            EmailAttributeName = "email",
            Active             = true,
            CreatedAt          = DateTimeOffset.UtcNow,
            UpdatedAt          = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(idp);
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallenge(challenge, project.HydraClientId, projectId: project.Id.ToString());

        var res = await fixture.NewSessionClient()
            .GetAsync($"/auth/saml/start?login_challenge={challenge}&idp_id={idp.Id}");

        res.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // No Set-Cookie is required for the flow to be resumable: the state is in Redis.
        var authnRequestId = ExtractAuthnRequestId(res.Headers.Location!.ToString());
        var otpCache = fixture.GetService<OtpCacheService>();
        // Record is "{idpId}|{loginChallenge}": ACS must bind the response to both.
        (await otpCache.PeekPendingAsync("saml_req", authnRequestId))
            .Should().Be($"{idp.Id}|{challenge}",
                "the pending AuthnRequest must be resolvable without the caller's session cookie, "
                + "and must carry the challenge it was issued for");
    }

    private static string ExtractAuthnRequestId(string redirectUrl)
    {
        // Inflate the deflated, base64-encoded SAMLRequest and pull out ID="...".
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(redirectUrl).Query);
        var raw = Convert.FromBase64String(query["SAMLRequest"].ToString());
        using var input = new MemoryStream(raw);
        using var inflate = new global::System.IO.Compression.DeflateStream(
            input, global::System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(inflate);
        var xml = reader.ReadToEnd();
        var match = global::System.Text.RegularExpressions.Regex.Match(xml, @"\bID=""([^""]+)""");
        match.Success.Should().BeTrue("the AuthnRequest must carry an ID");
        return match.Groups[1].Value;
    }

    // ── REG-FUNC-08: never offer a factor nothing can deliver ────────────────

    /// <summary>
    /// With no real SMS provider the stub silently drops the message. Offering SMS as the second
    /// factor told the user to enter a code that would never arrive — locking them out of their
    /// own account. The factor must not be offered unless a provider is configured.
    /// </summary>
    [Fact]
    public void SmsService_ReportsWhetherItCanActuallyDeliver()
    {
        // The production default (Program.cs) is this stub, which silently drops messages.
        var production = new RediensIAM.Services.StubSmsService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RediensIAM.Services.StubSmsService>.Instance);

        production.IsConfigured.Should().BeFalse(
            "callers must be able to tell that no SMS can actually be delivered");
    }

    // ── REG-FUNC-05: MFA enrolment mid-login needs no bearer token ───────────

    /// <summary>
    /// A project with RequireMfa sends a first-time user to enrolment before the login can
    /// finish. The login SPA has no access token at that point, so the enrolment endpoints must
    /// live under /auth (pending-MFA session) and not under /account (bearer-gated). Calling the
    /// /account route without a token returns 401 — which is exactly what the SPA used to do.
    /// </summary>
    [Fact]
    public async Task MfaSetupDuringLogin_CompletesWithoutBearerToken()
    {
        var (org, project, list) = await CreateTenantAsync();
        project.RequireMfa = true;
        await fixture.Db.SaveChangesAsync();

        var user = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        await fixture.FlushCacheAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, project.HydraClientId, project.Id.ToString(), org.Id.ToString());

        // One client throughout: the login SPA is same-origin with the API, so its session
        // cookie is fine here — what it does NOT have is a bearer token.
        var client = fixture.NewSessionClient();

        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = "P@ssw0rd!Test",
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("requires_mfa_setup").GetBoolean().Should().BeTrue();

        // The old path: /account/* is bearer-gated, so mid-login it can only 401.
        (await client.PostAsync("/account/mfa/totp/setup", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var start = await client.PostAsync("/auth/mfa/setup/totp/start", null);
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = (await start.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secret").GetString()!;

        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        var confirm = await client.PostAsJsonAsync("/auth/mfa/setup/totp/confirm", new { code });

        confirm.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("redirect_to").GetString().Should().NotBeNullOrEmpty(
            "enrolling the factor proves it, so the login completes in the same step");
        body.GetProperty("backup_codes").GetArrayLength().Should().Be(8);

        await fixture.RefreshDbAsync();
        (await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id))
            .TotpEnabled.Should().BeTrue();
    }
}
