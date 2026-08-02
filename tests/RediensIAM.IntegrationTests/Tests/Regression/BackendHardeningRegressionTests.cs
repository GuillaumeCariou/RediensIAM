using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Config;
using RediensIAM.Controllers;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Step 5 — R-10, R-20, T-N6, R-18, R-17 and the informational items acted on.
/// Each test pins a capability the pre-fix build had.
///
/// Stays under Regression: it crosses Org (SMTP endpoint validation), Webhooks (URL validator),
/// Api (introspect/authorize scoping), Auth (login budget), Middleware (Host header, admin API
/// gate) and Security (SSRF) — no one folder holds a majority of it.
/// </summary>
[Collection("RediensIAM")]
public class BackendHardeningRegressionTests(TestFixture fixture)
{
    private async Task<(Organisation Org, HttpClient Client)> OrgAdminClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin          = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        return (org, fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id)));
    }

    // ── R-10: per-org SMTP host and port were persisted unvalidated ──────────
    //
    // Webhooks, OIDC issuers and SAML metadata all pass WebhookUrlValidator; SMTP did not, and
    // POST /org/smtp/test connects synchronously and reported the exception message. That made
    // an org admin's SMTP settings a port scanner and banner oracle for everything the pod could
    // reach — including 100.64.0.0/10, which this deployment's mesh actually uses.

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]     // cloud metadata
    [InlineData("100.64.0.3")]          // the Tailscale admin ingress this deployment documents
    [InlineData("rediensiam-hydra-admin.default.svc")]
    [InlineData("localhost")]
    public async Task OrgSmtp_HostInsideTheMeshOrLoopback_IsRefusedAndNotPersisted(string host)
    {
        var (org, client) = await OrgAdminClientAsync();

        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host,
            port         = 587,
            start_tls    = true,
            from_address = "noreply@test.com",
            from_name    = "T",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("smtp_host_not_allowed");

        await fixture.RefreshDbAsync();
        fixture.Db.OrgSmtpConfigs.Any(c => c.OrgId == org.Id).Should().BeFalse(
            "a refused endpoint must leave nothing behind for /org/smtp/test to dial");
    }

    [Theory]
    [InlineData(22,    "smtp_port_not_allowed")]     // ssh
    [InlineData(6379,  "smtp_port_not_allowed")]     // the deployment's cache
    [InlineData(4445,  "smtp_port_not_allowed")]     // hydra admin
    [InlineData(0,     "smtp_port_not_allowed")]
    public async Task OrgSmtp_NonSubmissionPort_IsRefused(int port, string expected)
    {
        var (_, client) = await OrgAdminClientAsync();

        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.example.com", port, start_tls = true,
            from_address = "noreply@test.com", from_name = "T",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task OrgSmtp_CleartextSubmission_IsRefused()
    {
        var (_, client) = await OrgAdminClientAsync();

        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.example.com", port = 587, start_tls = false,
            username = "mailer", password = "s3cret",
            from_address = "noreply@test.com", from_name = "T",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("smtp_tls_required",
                "the org's SMTP credentials travel over this socket");
    }

    [Theory]
    [InlineData(587, true)]
    [InlineData(465, false)]    // implicit TLS — StartTls is meaningless on 465
    public async Task OrgSmtp_LegitimateEndpoint_IsStillAccepted(int port, bool startTls)
    {
        var (org, client) = await OrgAdminClientAsync();

        var res = await client.PutAsJsonAsync("/org/smtp", new
        {
            host = "smtp.example.com", port, start_tls = startTls,
            from_address = "noreply@test.com", from_name = "T",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.RefreshDbAsync();
        fixture.Db.OrgSmtpConfigs.Any(c => c.OrgId == org.Id).Should().BeTrue();
    }

    [Fact]
    public async Task OrgSmtpTest_Failure_DoesNotReturnTheExceptionText()
    {
        var (_, client) = await OrgAdminClientAsync();
        fixture.EmailStub.ThrowOnNextSend = new InvalidOperationException("Connection refused 100.64.0.3:25");

        var res  = await client.PostAsync("/org/smtp/test", null);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.TryGetProperty("detail", out _).Should().BeFalse(
            "the exception text distinguishes refused from filtered from an SMTP banner");
    }

    // ── R-20: SSRF re-validation was TOCTOU on DNS ──────────────────────────
    //
    // The validator resolved the name, then the socket stack resolved it again. A record with a
    // short TTL can answer "public" the first time and "internal" the second. The check now runs
    // inside the connect, on the address actually dialled.

    [Theory]
    [InlineData("http://127.0.0.1:9/")]
    [InlineData("http://localhost:9/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task SsrfSafeHandler_RefusesToDialAPrivateAddress(string url)
    {
        using var handler = WebhookUrlValidator.CreateSsrfSafeHandler();
        using var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        var act = async () => await client.GetAsync(url);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    /// <summary>
    /// Uri.Host keeps the brackets on an IPv6 literal, and Dns.GetHostAddressesAsync throws on
    /// the bracketed form — which the validator's catch block turned into "public, go ahead".
    /// </summary>
    [Theory]
    [InlineData("http://[::1]/hook")]
    [InlineData("http://[fd00::1]/hook")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    public async Task WebhookUrlValidator_BracketedIpv6Literal_IsRefused(string url)
    {
        (await WebhookUrlValidator.IsPrivateOrReservedAsync(url)).Should().BeTrue();
    }

    // ── T-N6: the introspection surface had no tenant scoping ───────────────

    private async Task<(HttpClient Client, Organisation Org, UserList List)> TenantGatewayAsync()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);
        var sa       = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "gateway", null, null);
        return (fixture.ClientWithToken(raw), org, list);
    }

    // `aud` is mandatory since the P-06 fix — the resource server names the tenant it serves.
    private static FormUrlEncodedContent Form(string token, Guid aud) =>
        new([new KeyValuePair<string, string>("token", token),
             new KeyValuePair<string, string>("aud", aud.ToString())]);

    [Fact]
    public async Task Introspect_TokenFromAnotherOrganisation_IsNotResolved()
    {
        var (client, _, _) = await TenantGatewayAsync();

        // A completely unrelated tenant's user token.
        var (victimOrg, victimList) = await fixture.Seed.CreateOrgAsync();
        var victimProject = await fixture.Seed.CreateProjectAsync(victimOrg.Id);
        var victim        = await fixture.Seed.CreateUserAsync(victimList.Id);
        var victimToken   = fixture.Seed.UserToken(victim.Id, victimOrg.Id, victimProject.Id);

        var body = await (await client.PostAsync("/api/introspect", Form(victimToken, victimProject.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeFalse(
            "one tenant's gateway credential resolved every token the deployment had issued");
        body.GetProperty("sub").ValueKind.Should().Be(JsonValueKind.Null);

        await fixture.RefreshDbAsync();
        fixture.Db.AuditLogs.Any(a => a.Action == "api.introspect.out_of_scope").Should().BeTrue(
            "this surface wrote no record at all, so enumeration left no trace");
    }

    [Fact]
    public async Task Introspect_TokenFromTheCallersOwnOrganisation_StillResolves()
    {
        var (client, org, list) = await TenantGatewayAsync();
        var subject  = await fixture.Seed.CreateServiceAccountAsync(list.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(subject.Id, "subject", null, null);

        var body = await (await client.PostAsync("/api/introspect", Form(raw, org.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeTrue();
        body.GetProperty("org_id").GetString().Should().Be(org.Id.ToString());
    }

    /// <summary>
    /// A service account on the __system__ user list carries no org_id and stays unscoped — that
    /// is the credential a genuinely multi-tenant gateway has to hold.
    ///
    /// Since the P-06 fix, "unscoped" means *any tenant it names*, not *every tenant at once*:
    /// the gateway still has to declare the audience it is serving on each call, and a token
    /// from a different tenant reads inactive. See ApiSurfaceIntrospectionTests.
    /// </summary>
    [Fact]
    public async Task Introspect_FromASystemServiceAccount_IsStillUnscoped()
    {
        var systemList = new UserList
        {
            Id = Guid.NewGuid(), Name = SeedData.UniqueName(), OrgId = null,
            Immovable = true, CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.UserLists.Add(systemList);
        await fixture.Db.SaveChangesAsync();

        var sa       = await fixture.Seed.CreateServiceAccountAsync(systemList.Id);
        var (raw, _) = await fixture.GetService<PatService>().GenerateAsync(sa.Id, "system-gateway", null, null);
        var client   = fixture.ClientWithToken(raw);

        var (org, list) = await fixture.Seed.CreateOrgAsync();
        var project     = await fixture.Seed.CreateProjectAsync(org.Id);
        var user        = await fixture.Seed.CreateUserAsync(list.Id);
        var token       = fixture.Seed.UserToken(user.Id, org.Id, project.Id);

        var body = await (await client.PostAsync("/api/introspect", Form(token, project.Id)))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("active").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// System:rediensiam#super_admin holds the deployment's administrator list. A tenant
    /// credential asking about it is enumerating, never authorising.
    /// </summary>
    [Fact]
    public async Task Authorize_TenantCallerProbingTheSystemNamespace_IsRefused()
    {
        var (client, org, list) = await TenantGatewayAsync();
        var user  = await fixture.Seed.CreateUserAsync(list.Id);
        var token = $"tn6-{user.Id:N}";
        fixture.Hydra.RegisterToken(token, user.Id.ToString(), org.Id.ToString(), null, []);
        fixture.Keto.AllowAll();

        var res = await client.PostAsJsonAsync("/api/authorize", new
        {
            token,
            @namespace = Roles.KetoSystemNamespace,
            @object    = Roles.KetoSystemObject,
            relation   = Roles.KetoSuperAdminRelation,
            aud        = org.Id,
        });

        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("allowed").GetBoolean().Should().BeFalse(
                "Keto would have said yes — the refusal has to happen before the probe");

        await fixture.RefreshDbAsync();
        fixture.Db.AuditLogs.Any(a => a.Action == "api.authorize.out_of_scope").Should().BeTrue();
    }

    // ── R-18: the per-user rate-limit counter was written but never read ────

    /// <summary>
    /// Every failure wrote rate:login:user:{id}; the login path only ever consulted
    /// rate:login:{ip}. An attacker rotating source addresses therefore got a fresh budget per
    /// address against the same account.
    /// </summary>
    [Fact]
    public async Task Login_WhenThePerUserBudgetIsSpentFromOtherAddresses_IsRefused()
    {
        await fixture.FlushCacheAsync();
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var project        = await fixture.Seed.CreateProjectAsync(org.Id);
        var list           = await fixture.Seed.CreateUserListAsync(org.Id);
        project.AssignedUserListId = list.Id;
        await fixture.Db.SaveChangesAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id, password: SeedData.DefaultPassword);

        // Failures charged from addresses the test client never uses.
        var limiter = fixture.GetService<LoginRateLimiter>();
        for (var i = 0; i < 5; i++)
            await limiter.RecordFailureAsync($"203.0.113.{i}", user.Id);

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(challenge, project.HydraClientId,
            project.Id.ToString(), org.Id.ToString());

        var res = await fixture.Client.PostAsJsonAsync("/auth/login", new
        {
            login_challenge = challenge,
            email           = user.Email,
            password        = SeedData.DefaultPassword,
        });

        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the budget is per account as well as per address");
        await fixture.FlushCacheAsync();
    }

    // ── R-17: AllowedHosts "*" disabled host filtering entirely ─────────────

    [Fact]
    public async Task Request_WithAForeignHostHeader_IsRefused()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "http://localhost/health");
        req.Headers.Host = "attacker.example.com";

        var res = await fixture.Client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unfiltered Host header is a cache-poisoning and log-injection primitive");
    }

    [Fact]
    public async Task Request_WithTheDeploymentsOwnHost_IsStillServed()
    {
        (await fixture.Client.GetAsync("http://localhost/health")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // ── I-02: /admin GET without an Authorization header skipped the middleware ──

    /// <summary>
    /// The bypass was safe only because every /admin controller happens to carry
    /// [RequireManagementLevel]; one forgotten attribute was an unauthenticated GET. Routing has
    /// already run when the branch is evaluated, so a request that resolved to a controller
    /// action is now rejected before reaching it — the empty body is what distinguishes the
    /// middleware's refusal from the filter's.
    /// </summary>
    [Fact]
    public async Task AdminApiGet_WithoutAuthorization_IsRefusedBeforeTheController()
    {
        var res = await fixture.Client.GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadAsStringAsync()).Should().BeEmpty(
            "the request must not reach controller code at all");
    }

    [Fact]
    public async Task AdminSpaNavigation_WithoutAuthorization_StillLoads()
    {
        (await fixture.Client.GetAsync("/console/config")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── I-11: SsoUrl was not scheme-validated ───────────────────────────────

    /// <summary>
    /// GET /auth/saml/start redirects to SsoUrl without passing through RedirectValidator, so an
    /// org admin could turn it into an unauthenticated open redirect to any origin.
    /// </summary>
    [Theory]
    [InlineData("http://evil.example.com/sso")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example.com/sso")]
    public async Task SamlConfig_NonHttpsSsoUrl_IsRefused(string ssoUrl)
    {
        var svc = new SamlService(null!, NullLogger<SamlService>.Instance);
        var idp = new SamlIdpConfig
        {
            Id = Guid.NewGuid(), EntityId = "https://idp.example.com", SsoUrl = ssoUrl,
            CertificatePem = null,
        };

        var act = async () => await svc.BuildConfigAsync(
            idp, "https://sp.example.com/saml/metadata", new Uri("https://sp.example.com/saml/acs"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
