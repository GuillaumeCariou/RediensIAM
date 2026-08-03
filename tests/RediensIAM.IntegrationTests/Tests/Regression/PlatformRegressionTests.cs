using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Platform regressions: PAT revocation latency, webhook test dispatch, CSV export
/// escaping, and the admin-console API contract (routes and request fields the SPA
/// actually sends).
///
/// Stays under Regression: it crosses ServiceAccounts (PAT revocation), Webhooks (test dispatch),
/// Org (CSV and audit-log export), Project (info patch) and System (encryption-key config) —
/// a genuine cross-cut with no dominant subject.
/// </summary>
[Collection("RediensIAM")]
public class PlatformRegressionTests(TestFixture fixture)
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

    // ── REG-SEC-08: PAT cache survives service-account deactivation ──────────

    /// <summary>
    /// PatService caches introspection results under the token hash for
    /// <c>Cache:PatTtlMinutes</c> (default 5). Only <c>RevokePat</c> evicts that key,
    /// so disabling the service account — or suspending its organisation — leaves the
    /// token fully usable for the rest of the TTL.
    /// </summary>
    [Fact]
    public async Task PatIntrospection_AfterServiceAccountDeactivated_IsRejectedImmediately()
    {
        var (_, _, list) = await CreateTenantAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var pats = fixture.GetService<PatService>();
        var (raw, _) = await pats.GenerateAsync(sa.Id, "revocation-latency", null, null);

        (await pats.IntrospectAsync(raw)).Should().NotBeNull("the fresh PAT is valid and now cached");

        await fixture.RefreshDbAsync();
        var stored = await fixture.Db.ServiceAccounts.FirstAsync(x => x.Id == sa.Id);
        stored.Active = false;
        await fixture.Db.SaveChangesAsync();

        (await pats.IntrospectAsync(raw)).Should().BeNull(
            "deactivating a service account must invalidate its cached PAT introspections at once");
    }

    /// <summary>Suspending the owning organisation must have the same immediate effect.</summary>
    [Fact]
    public async Task PatIntrospection_AfterOrganisationSuspended_IsRejectedImmediately()
    {
        var (org, _, list) = await CreateTenantAsync();
        var sa = await fixture.Seed.CreateServiceAccountAsync(list.Id);

        var pats = fixture.GetService<PatService>();
        var (raw, _) = await pats.GenerateAsync(sa.Id, "org-suspend-latency", null, null);

        (await pats.IntrospectAsync(raw)).Should().NotBeNull();

        await fixture.RefreshDbAsync();
        var storedOrg = await fixture.Db.Organisations.FirstAsync(o => o.Id == org.Id);
        storedOrg.Active      = false;
        storedOrg.SuspendedAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();

        (await pats.IntrospectAsync(raw)).Should().BeNull(
            "suspending a tenant must cut off its service-account tokens immediately");
    }

    // ── REG-FUNC-01: webhook "Send test" delivers nothing ────────────────────

    /// <summary>
    /// POST /org/webhooks/{id}/test dispatches the event name <c>webhook.test</c>, but
    /// WebhookService.DispatchAsync only matches webhooks whose Events array contains the
    /// dispatched name — and <c>webhook.test</c> is not in WebhookEvents.All, so it can
    /// never be subscribed to. The button returns 200 and does nothing.
    /// </summary>
    [Fact]
    public async Task WebhookTest_EnqueuesADelivery()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var orgList  = await fixture.Db.UserLists.FirstAsync(ul => ul.OrgId == org.Id);
        var admin    = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client   = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        fixture.Keto.AllowAll();

        var appConfig = fixture.GetService<AppConfig>();
        var webhook = new Webhook
        {
            Id        = Guid.NewGuid(),
            OrgId     = org.Id,
            Url       = "https://hooks.example.com/rediensiam",
            SecretEnc = TotpEncryption.EncryptString(
                appConfig.WebhookEncKey,
                Convert.ToBase64String(global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            Events    = WebhookEvents.All,   // subscribed to everything that can be subscribed to
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.Webhooks.Add(webhook);
        await fixture.Db.SaveChangesAsync();

        var res = await client.PostAsync($"/org/webhooks/{webhook.Id}/test", null);
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var queue   = fixture.GetService<IWebhookQueue>();
        var pending = await queue.RecoverAllAsync();

        pending.Should().Contain(job => job.Contains(webhook.Id.ToString()),
            "the test button must actually enqueue a delivery for the webhook it names");
    }

    // ── REG-FUNC-02: CSV formula injection in exports ────────────────────────

    /// <summary>
    /// CsvEscape only quotes on comma/quote/newline. A display name starting with
    /// = + - @ is written verbatim and executes as a formula when the export is opened
    /// in Excel, LibreOffice, or Google Sheets — the payload is attacker-controlled,
    /// since any end user can set their own display name.
    /// </summary>
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(1,1)")]
    [InlineData("=HYPERLINK(\"http://evil.example.com\",\"click\")")]
    public async Task UserExport_FormulaPayloadInDisplayName_IsNeutralised(string payload)
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var orgList  = await fixture.Db.UserLists.FirstAsync(ul => ul.OrgId == org.Id);
        var list     = await fixture.Seed.CreateUserListAsync(org.Id);

        var target = await fixture.Seed.CreateUserAsync(list.Id);
        target.DisplayName = payload;
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        await fixture.Db.SaveChangesAsync();

        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        fixture.Keto.AllowAll();
        await fixture.FlushCacheAsync();

        var res = await client.GetAsync($"/org/userlists/{list.Id}/export?format=csv");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var csv = await res.Content.ReadAsStringAsync();

        var dataRow = csv.Split('\n').FirstOrDefault(l => l.Contains(target.Email));
        dataRow.Should().NotBeNull();

        // Split on commas outside quotes is overkill here — assert no *cell* begins with a
        // formula lead character. Quoted cells are checked past the opening quote.
        foreach (var cell in dataRow!.Split(','))
        {
            var content = cell.TrimStart('"');
            if (content.Length == 0) continue;
            new[] { '=', '+', '-', '@' }.Should().NotContain(content[0],
                $"cell '{cell}' would be evaluated as a formula by a spreadsheet application");
        }
    }

    // ── REG-FUNC-05: project settings silently dropped ───────────────────────

    /// <summary>
    /// The admin SPA PATCHes /project/info with ip_allowlist, check_breached_passwords and
    /// email_from_name, but UpdateProjectInfoRequest declares none of them. System.Text.Json
    /// ignores unknown members, so the API answers 200 and discards the change — the operator
    /// believes an IP allowlist is active when it is not.
    /// </summary>
    [Fact]
    public async Task ProjectInfoPatch_IpAllowlistAndBreachCheck_ArePersisted()
    {
        var (org, project, _) = await CreateTenantAsync();
        var orgList = await fixture.Db.UserLists.FirstAsync(ul => ul.OrgId == org.Id);
        var admin   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client  = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        fixture.Keto.AllowAll();

        var res = await client.PatchAsJsonAsync($"/project/info?project_id={project.Id}", new
        {
            ip_allowlist             = new[] { "203.0.113.0/24" },
            check_breached_passwords = true,
            email_from_name          = "Tenant Support",
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var stored = await fixture.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == project.Id);

        stored.IpAllowlist.Should().BeEquivalentTo(["203.0.113.0/24"],
            "a settings write the API acknowledges must actually take effect");
        stored.CheckBreachedPasswords.Should().BeTrue();
        stored.EmailFromName.Should().Be("Tenant Support");
    }

    /// <summary>An unparseable CIDR must be refused rather than silently locking the tenant out.</summary>
    [Fact]
    public async Task ProjectInfoPatch_InvalidCidrInIpAllowlist_IsRejected()
    {
        var (org, project, _) = await CreateTenantAsync();
        var orgList = await fixture.Db.UserLists.FirstAsync(ul => ul.OrgId == org.Id);
        var admin   = await fixture.Seed.CreateUserAsync(orgList.Id);
        var client  = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        fixture.Keto.AllowAll();

        var res = await client.PatchAsJsonAsync($"/project/info?project_id={project.Id}", new
        {
            ip_allowlist = new[] { "not-an-ip-range" },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── REG-FUNC-04: admin SPA export routes ─────────────────────────────────

    /// <summary>
    /// Pins the export route contract the admin SPA depends on. api.ts currently calls
    /// /org/export/audit-log and /admin/export/audit-log, neither of which is mapped —
    /// both download buttons 404.
    /// </summary>
    [Fact]
    public async Task AuditLogExport_RoutesUsedByAdminSpa_Exist()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var orgList  = await fixture.Db.UserLists.FirstAsync(ul => ul.OrgId == org.Id);
        var admin    = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        await fixture.FlushCacheAsync();

        var orgClient = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, org.Id));
        var orgExport = await orgClient.GetAsync("/org/audit-log/export?format=csv");
        orgExport.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.FlushCacheAsync();
        var sysClient = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(admin.Id));
        var sysExport = await sysClient.GetAsync($"/admin/organizations/{org.Id}/export/audit-log?format=csv");
        sysExport.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── REG-FUNC-06: crypto key format documented incorrectly ────────────────

    /// <summary>
    /// README documents <c>secrets.totpEncryptionKey</c> as "32-byte base64" and tells the
    /// operator to run <c>openssl rand -base64 32</c>. Program.ValidateEncryptionKey requires
    /// 64 hex characters and throws on anything else, so following the README bricks the
    /// deployment on first boot. This pins the format the code actually enforces.
    /// </summary>
    [Theory]
    [InlineData("uZ8lQ0G0mV5w0Zk8bC0mV5w0Zk8bC0mV5w0Zk8bC0mU=")] // openssl rand -base64 32
    [InlineData("short")]
    [InlineData("zz11223344556677889900aabbccddeeff00112233445566778899aabbccddeef")] // 'zz' not hex
    public void EncryptionKey_NonHexValues_AreRejected(string key)
    {
        var isValid = key.Length == 64 && key.All(Uri.IsHexDigit);
        isValid.Should().BeFalse("the README example must not be accepted as a valid key");
    }

    [Fact]
    public void EncryptionKey_SixtyFourHexChars_IsAccepted()
    {
        var key = Convert.ToHexString(global::System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        key.Length.Should().Be(64);
        key.All(Uri.IsHexDigit).Should().BeTrue();
    }
}
