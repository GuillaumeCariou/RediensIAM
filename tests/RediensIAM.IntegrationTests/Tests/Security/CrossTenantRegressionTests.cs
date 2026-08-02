using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// Tenant-isolation regressions.
///
/// The IAM resolves the target project from <c>project_id</c> carried in the OAuth2
/// authorize request (oidc_context.extra, falling back to the raw request URL).
/// That value is attacker-controlled: any party who can start an authorize flow on
/// a client they own can name ANY project GUID in the deployment.
///
/// <c>POST /auth/login</c> cross-checks it against <c>client.metadata.project_id</c>
/// and rejects a mismatch. The other project-scoped entry points did not, so a
/// tenant could read another tenant's login configuration and — via the social
/// login start/callback pair — drive a login for a foreign project onto its own
/// login_challenge.
/// </summary>
[Collection("RediensIAM")]
public class CrossTenantRegressionTests(TestFixture fixture)
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

    // ── REG-SEC-02a: theme/config disclosure across tenants ──────────────────

    /// <summary>
    /// Victim and attacker each own a project. The attacker starts an authorize flow on
    /// their own Hydra client but names the victim's project in oidc_context.extra.
    /// GET /auth/login/theme must refuse — the project named in the request has to belong
    /// to the client that opened the flow.
    /// </summary>
    [Fact]
    public async Task LoginTheme_ProjectIdNotOwnedByClient_IsRejected()
    {
        var victim   = await CreateTenantAsync();
        var attacker = await CreateTenantAsync();

        victim.Project.Name = "VICTIM-TENANT-INTERNAL-NAME";
        victim.Project.LoginTheme = new Dictionary<string, object>
        {
            ["primary_color"] = "#victim",
            ["providers"]     = JsonSerializer.Deserialize<JsonElement>(
                """[{"id":"google","type":"google","enabled":true,"client_id":"victim-google-client-id"}]"""),
        };
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        // Registered (real) project = attacker's. Requested project = victim's.
        fixture.Hydra.SetupLoginChallengeWithProjectIdMismatch(
            challenge,
            attacker.Project.HydraClientId,
            oidcProjectId:       victim.Project.Id.ToString(),
            registeredProjectId: attacker.Project.Id.ToString());

        var res  = await fixture.Client.GetAsync($"/auth/login/theme?login_challenge={challenge}");
        var body = await res.Content.ReadAsStringAsync();

        body.Should().NotContain("VICTIM-TENANT-INTERNAL-NAME",
            "the login theme of a project the calling client does not own must never be returned");
        body.Should().NotContain("victim-google-client-id");
        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    /// <summary>Same check on the login-page bootstrap endpoint.</summary>
    [Fact]
    public async Task LoginPageInfo_ProjectIdNotOwnedByClient_IsRejected()
    {
        var victim   = await CreateTenantAsync();
        var attacker = await CreateTenantAsync();

        victim.Project.Name = "VICTIM-TENANT-LOGIN-PAGE";
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProjectIdMismatch(
            challenge,
            attacker.Project.HydraClientId,
            oidcProjectId:       victim.Project.Id.ToString(),
            registeredProjectId: attacker.Project.Id.ToString());

        var res  = await fixture.Client.GetAsync($"/auth/login?login_challenge={challenge}");
        var body = await res.Content.ReadAsStringAsync();

        body.Should().NotContain("VICTIM-TENANT-LOGIN-PAGE");
        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── REG-SEC-02b: social-login start bound to a foreign project ───────────

    /// <summary>
    /// The dangerous variant: /auth/oauth2/start builds the provider redirect from the
    /// *victim's* configured IdP credentials and stores the attacker's login_challenge
    /// in the OAuth state. The callback then accepts the login for the victim's project
    /// against the attacker's client — a cross-tenant authorization code.
    /// The start endpoint must refuse a project the calling client does not own.
    /// </summary>
    [Fact]
    public async Task OAuthStart_ProjectIdNotOwnedByClient_IsRejected()
    {
        var victim   = await CreateTenantAsync();
        var attacker = await CreateTenantAsync();

        victim.Project.LoginTheme = new Dictionary<string, object>
        {
            ["providers"] = JsonSerializer.Deserialize<JsonElement>(
                """[{"id":"google","type":"google","enabled":true,"client_id":"victim-google-client-id","client_secret":"s3cr3t"}]"""),
        };
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProjectIdMismatch(
            challenge,
            attacker.Project.HydraClientId,
            oidcProjectId:       victim.Project.Id.ToString(),
            registeredProjectId: attacker.Project.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/oauth2/start?login_challenge={challenge}&provider_id=google");

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400,
            "starting a social flow for a foreign project yields a cross-tenant authorization code");
        res.Headers.Location?.ToString().Should().NotContain("victim-google-client-id");
    }

    // ── REG-SEC-02c: SAML start bound to a foreign project ───────────────────

    /// <summary>
    /// /auth/saml/start accepts any idp_id and never checks that the IdP's project matches
    /// the project of the client that opened the login_challenge. Same cross-tenant code
    /// issuance as the social variant, via an enterprise IdP instead.
    /// </summary>
    [Fact]
    public async Task SamlStart_IdpFromForeignProject_IsRejected()
    {
        var victim   = await CreateTenantAsync();
        var attacker = await CreateTenantAsync();

        var idp = new SamlIdpConfig
        {
            Id                 = Guid.NewGuid(),
            ProjectId          = victim.Project.Id,
            EntityId           = "https://victim-idp.example.com/metadata",
            SsoUrl             = "https://victim-idp.example.com/sso",
            EmailAttributeName = "email",
            JitProvisioning    = true,
            Active             = true,
            CreatedAt          = DateTimeOffset.UtcNow,
            UpdatedAt          = DateTimeOffset.UtcNow,
        };
        fixture.Db.SamlIdpConfigs.Add(idp);
        await fixture.Db.SaveChangesAsync();

        var challenge = Guid.NewGuid().ToString("N");
        fixture.Hydra.SetupLoginChallengeWithProject(
            challenge, attacker.Project.HydraClientId,
            attacker.Project.Id.ToString(), attacker.Org.Id.ToString());

        var res = await fixture.Client.GetAsync(
            $"/auth/saml/start?login_challenge={challenge}&idp_id={idp.Id}");

        ((int)res.StatusCode).Should().BeGreaterThanOrEqualTo(400,
            "an IdP belonging to another tenant's project must not be usable from this client's flow");
        res.Headers.Location?.ToString().Should().NotContain("victim-idp.example.com");
    }

    // ── REG-SEC-01: token audience confusion ─────────────────────────────────

    /// <summary>
    /// HydraService.ValidateJwtAsync introspects the bearer token and reads roles straight
    /// out of <c>ext</c>, ignoring <c>client_id</c> and <c>aud</c>. A token minted for a
    /// tenant's own application client is therefore accepted verbatim by the IAM management
    /// API. Only tokens issued to the admin console client may reach /admin.
    /// </summary>
    [Fact]
    public async Task AdminApi_TokenIssuedToTenantClient_IsRejected()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();

        var token = $"foreign-aud-{Guid.NewGuid():N}";
        fixture.Hydra.RegisterTokenForClient(
            token, user.Id.ToString(), org.Id.ToString(), null,
            roles:    [Roles.SuperAdmin],
            clientId: "client_some_tenant_app",
            audience: ["https://tenant-app.example.com"]);

        var res = await fixture.ClientWithToken(token).GetAsync("/admin/organizations");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    /// <summary>The admin console client's own token must keep working.</summary>
    [Fact]
    public async Task AdminApi_TokenIssuedToAdminClient_IsAccepted()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();

        var token = $"admin-aud-{Guid.NewGuid():N}";
        fixture.Hydra.RegisterTokenForClient(
            token, user.Id.ToString(), null, null,
            roles:    [Roles.SuperAdmin],
            clientId: Roles.AdminClientId,
            audience: [Roles.AdminClientId]);

        var res = await fixture.ClientWithToken(token).GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── REG-SEC-11: authorisation must be live, not a token snapshot ─────────

    /// <summary>
    /// RequireManagementLevelAttribute used to authorise purely from <c>ext.roles</c>, a snapshot
    /// taken at token issuance. Revoking super_admin — or suspending the org — therefore had no
    /// effect until the token expired. The same token must stop working once Keto no longer
    /// grants the role.
    /// </summary>
    [Fact]
    public async Task ManagementApi_AfterRoleRevokedInKeto_IsRejected()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);
        await fixture.FlushCacheAsync();

        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));

        (await client.GetAsync("/admin/organizations")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Role revoked upstream. The bearer token is untouched and still carries super_admin.
        fixture.Keto.DenyAll();
        await fixture.FlushCacheAsync();   // skip the 30s decision cache

        var res = await client.GetAsync("/admin/organizations");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a revoked role must take effect without waiting for the token to expire");
    }

    /// <summary>
    /// Keto being unreachable must not fall back to trusting the token's own claims.
    /// </summary>
    [Fact]
    public async Task ManagementApi_WhenKetoIsUnreachable_FailsClosed()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);
        await fixture.FlushCacheAsync();

        fixture.Keto.SimulateOutage();
        try
        {
            var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));
            var res = await client.GetAsync("/admin/organizations");

            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            fixture.Keto.AllowAll();
            await fixture.FlushCacheAsync();
        }
    }

    /// <summary>
    /// Hydra's introspection endpoint reports refresh tokens as active when no
    /// <c>token_type_hint</c> is supplied. A refresh token must never authenticate an API call.
    /// </summary>
    [Fact]
    public async Task ProtectedApi_RefreshTokenPresentedAsBearer_IsRejected()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user  = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();

        var token = $"refresh-{Guid.NewGuid():N}";
        fixture.Hydra.RegisterRefreshToken(token, user.Id.ToString(), [Roles.SuperAdmin]);

        var res = await fixture.ClientWithToken(token).GetAsync("/account/me");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
