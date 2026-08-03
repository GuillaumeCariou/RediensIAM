using RediensIAM.Config;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// Step 15a — the residuals step 14's ledger recorded as open or as closed-but-untested.
///
/// Three of these cover fixes that already existed and had no test (P-03, P-08). That is the
/// P-02 failure mode: a guard nothing exercises is a guard a refactor can delete with the suite
/// still green. The rest cover the four authentication defaults (T-07a–d).
///
/// Stays under Regression: it crosses Project (theme validation), Org (suspension, SMTP),
/// Auth (password floor, phone enrolment) and Account (password change) — the file is a ledger
/// of leftovers, not a subject.
/// </summary>
[Collection("RediensIAM")]
public class ResidualFindingsTests(TestFixture fixture)
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

    // ── P-03: the theme-key walk, which nothing asserted ─────────────────────

    /// <summary>
    /// LoginThemeValidator refuses the CSS exfiltration primitive in every theme value, not just
    /// in custom_css: the login page pushes each recognised string into a CSS custom property and
    /// index.css spends several of them inside `background: var(--surface)`, which takes `url()`.
    /// Step 11b closed it by inspection and no test ever named the error code, so a refactor that
    /// restored the early return after custom_css would re-arm chain C-6 with the suite green.
    /// </summary>
    [Theory]
    [InlineData("url(https://evil.invalid/k)")]
    [InlineData("#fff;background:url(https://evil.invalid/k)")]
    [InlineData("<script>")]
    [InlineData("attr(value)")]
    public async Task ProjectInfo_ThemeValueCarryingACssPrimitive_IsRefused(string value)
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PatchAsJsonAsync("/project/info",
            new { login_theme = new { surface = value } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("theme_value_invalid_character");

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == tenant.Project.Id);
        (stored.LoginTheme?.ContainsKey("surface") ?? false).Should().BeFalse(
            "a refused theme must not be half-applied");
    }

    /// <summary>The length ceiling is the other half of the same guard.</summary>
    [Fact]
    public async Task ProjectInfo_OverlongThemeValue_IsRefused()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PatchAsJsonAsync("/project/info",
            new { login_theme = new { surface = new string('a', 121) } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("theme_value_invalid_character");
    }

    /// <summary>The org route reaches the same validator; both must walk the non-CSS keys.</summary>
    [Fact]
    public async Task OrgProjectUpdate_ThemeValueCarryingACssPrimitive_IsRefused()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));

        var res = await client.PatchAsJsonAsync($"/org/projects/{tenant.Project.Id}",
            new { login_theme = new { accent = "url(https://evil.invalid/k)" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("theme_value_invalid_character");
    }

    /// <summary>An ordinary colour must still go through — the guard is a filter, not a wall.</summary>
    [Fact]
    public async Task ProjectInfo_OrdinaryThemeValue_IsStillAccepted()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(
            fixture.Seed.ProjectManagerToken(admin.Id, tenant.Org.Id, tenant.Project.Id));

        var res = await client.PatchAsJsonAsync("/project/info",
            new { login_theme = new { surface = "#123456", accent = "rebeccapurple" } });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == tenant.Project.Id);
        stored.LoginTheme.Should().ContainKey("surface");
    }

    // ── P-08 residual: suspension must remove authority, not just sessions ───

    /// <summary>
    /// AssignOrgAdmin takes any user id, so an org's administrator need not belong to it. Such a
    /// user is invisible to every `UserList.OrgId == orgId` query, logs in through AdminLogin —
    /// which consults no organisation — and the token they get back carries the Keto tuple that
    /// suspension never removed. Revoking their session only forced a re-login.
    /// </summary>
    [Fact]
    public async Task SuspendedOrg_SystemListOrgAdmin_LosesManagementAccess()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant     = await CreateTenantAsync();
        var systemList = await CreateSystemListAsync();
        var admin      = await fixture.Seed.CreateUserAsync(systemList.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);

        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));
        (await client.GetAsync("/org/info")).StatusCode.Should().Be(HttpStatusCode.OK);

        var org = await fixture.Db.Organisations.FirstAsync(o => o.Id == tenant.Org.Id);
        org.Active = false;
        await fixture.Db.SaveChangesAsync();
        await fixture.FlushCacheAsync();   // drop the 30 s live-authorisation decision

        (await client.GetAsync("/org/info")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a new token minted after suspension carries the same surviving grant, so the "
            + "lock-out has to happen on the request, not at login");
    }

    /// <summary>
    /// The carve-out that makes the fix above safe. super_admin is the level that unsuspends, and
    /// its live check takes no organisation — get that exemption wrong and suspension becomes
    /// irreversible, which is why 11b §4 declined to make the change without this test.
    /// </summary>
    [Fact]
    public async Task SuspendedOrg_SuperAdmin_CanStillUnsuspendIt()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var actor  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(fixture.Seed.SuperAdminToken(actor.Id));

        (await client.PostAsync($"/admin/organizations/{tenant.Org.Id}/suspend", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await fixture.FlushCacheAsync();

        (await client.PostAsync($"/admin/organizations/{tenant.Org.Id}/unsuspend", null))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "suspension must not lock out the only role that can reverse it");

        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Organisations.AsNoTracking().FirstAsync(o => o.Id == tenant.Org.Id))
            .Active.Should().BeTrue();
    }

    // ── T-07a: the ASVS L2 password floor, and the paths that bypassed it ────

    [Fact]
    public void PasswordFloor_IsTheAsvsL2Minimum() =>
        PasswordPolicyService.AbsoluteMinimumLength.Should().Be(12);

    /// <summary>
    /// The floor applies to every path that writes a hash. The admin-driven creation paths read
    /// no policy at all — they checked `MinPasswordLength > 0` or nothing — so a seeded account
    /// could start below the minimum every self-service path enforces and keep that password
    /// indefinitely, because nothing re-evaluates it after the write.
    /// </summary>
    [Fact]
    public async Task AdminCreatedUser_WithAPasswordBelowTheFloor_IsRefused()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));

        var email = SeedData.UniqueEmail();
        var res = await client.PostAsJsonAsync($"/org/userlists/{tenant.List.Id}/users",
            new { email, password = "P@ssw0rd!1" });   // 10 characters

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("password_too_short");
        body.GetProperty("min_length").GetInt32().Should().Be(12);

        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Users.AsNoTracking().AnyAsync(u => u.Email == email))
            .Should().BeFalse("the refusal must happen before the row is written");
    }

    [Fact]
    public async Task AdminSetPassword_BelowTheFloor_IsRefusedAndNotPersisted()
    {
        await fixture.FlushCacheAsync();
        fixture.Keto.AllowAll();
        var tenant = await CreateTenantAsync();
        var admin  = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var target = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        await fixture.Seed.CreateOrgRoleAsync(tenant.Org.Id, admin.Id, Roles.OrgAdmin);
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(admin.Id, tenant.Org.Id));
        var originalHash = target.PasswordHash;

        var res = await client.PatchAsJsonAsync($"/org/users/{target.Id}",
            new { new_password = "P@ssw0rd!1" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id))
            .PasswordHash.Should().Be(originalHash);
    }

    // ── T-07b/c: the two tenant defaults ─────────────────────────────────────

    /// <summary>
    /// Breached-password checking is opt-out: it costs the user nothing, so a tenant should not
    /// get the weaker setting by not looking. MFA is opt-*in* by product decision — forcing a
    /// second factor on every new tenant is a UX call that belongs to the tenant's owner. The
    /// create paths never write either field, so the entity initialiser is the whole policy.
    /// </summary>
    [Fact]
    public async Task ANewProject_ChecksBreachedPasswords_ButDoesNotForceMfa_ByDefault()
    {
        var (org, _) = await fixture.Seed.CreateOrgAsync();
        var project  = await fixture.Seed.CreateProjectAsync(org.Id);

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == project.Id);
        stored.CheckBreachedPasswords.Should().BeTrue();
        stored.RequireMfa.Should().BeFalse();
    }

    // ── T-07d: an undeliverable factor must not be enrollable ────────────────

    /// <summary>
    /// The production ISmsService is a stub that logs and drops. Login and registration already
    /// refuse to offer SMS when it cannot deliver; enrolment did not, so a user could attach a
    /// phone factor, be shown "code sent", and — on a project that requires MFA — be locked out
    /// by their own second factor.
    /// </summary>
    [Fact]
    public async Task PhoneEnrolment_WithNoRealSmsProvider_IsRefused()
    {
        await fixture.FlushCacheAsync();
        var tenant = await CreateTenantAsync();
        var user   = await fixture.Seed.CreateUserAsync(tenant.List.Id);
        var client = fixture.ClientWithToken(
            fixture.Seed.UserToken(user.Id, tenant.Org.Id, tenant.Project.Id));

        fixture.SmsStub.IsConfigured = false;
        try
        {
            var res = await client.PostAsJsonAsync("/account/mfa/phone/setup",
                new { phone = "+33600000009" });

            res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await res.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("error").GetString().Should().Be("sms_provider_not_configured");
            fixture.SmsStub.SentMessages.Should().NotContain(m => m.To == "+33600000009");
        }
        finally { fixture.SmsStub.IsConfigured = true; }
    }

    /// <summary>
    /// The login page applies the same guard client-side (<c>THEME_VALUE_FORBIDDEN</c> and
    /// <c>THEME_VALUE_MAX_LENGTH</c> in <c>frontend/login/src/lib/sanitizeCss.ts</c>), and the two
    /// cannot share a literal across languages. Both sides pin it instead: widen this guard alone
    /// and this test fails; widen the client alone and its own pinning test fails.
    /// </summary>
    [Fact]
    public void TheClientAndServerThemeValueGuardsAgree()
    {
        LoginThemeValidator.ForbiddenValueCharacters.Should().Be(";{}()<>\"'`\\");
        LoginThemeValidator.MaxThemeValueLength.Should().Be(120);
    }

    /// <summary>
    /// The theme validator walked the dictionary's top level only.
    ///
    /// <para>
    /// <c>AsString</c> returns null for a JSON array, so the <c>providers</c> list — which the
    /// login page renders, one <c>&lt;img src&gt;</c> per entry — was skipped entirely. The
    /// top-level <c>logo_url</c> is required to be HTTPS; the per-provider one got no check at all,
    /// so a tenant admin could point every user's login page at a URL of their choosing and learn
    /// who signs in, when, and from which address. The page is unauthenticated, so the tenant does
    /// not even need the victim to have an account with them.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("http://attacker.test/beacon.png")]
    [InlineData("javascript:alert(1)")]
    public void ThemeValidation_ReachesIntoTheProvidersArray(string hostileLogo)
    {
        var theme = new Dictionary<string, object>
        {
            ["providers"] = JsonSerializer.SerializeToElement(new[]
            {
                new { id = "google", type = "google", label = "Google", logo_url = hostileLogo },
            }),
        };

        LoginThemeValidator.Validate(theme).Should().NotBeNull(
            "a logo URL inside providers[] reaches the same <img src> as the top-level one");
    }

    [Fact]
    public void ThemeValidation_StillAcceptsAnHttpsProviderLogo()
    {
        var theme = new Dictionary<string, object>
        {
            ["providers"] = JsonSerializer.SerializeToElement(new[]
            {
                new { id = "google", type = "google", label = "Google", logo_url = "https://cdn.example.com/g.png" },
            }),
        };

        LoginThemeValidator.Validate(theme).Should().BeNull();
    }

    /// <summary>
    /// The org SMTP host is checked when it is written and never again.
    ///
    /// <para>
    /// <c>SmtpEndpointValidator</c> runs in the two controllers that accept the value; the send
    /// path re-read the stored row and handed the host straight to MailKit. So a host that
    /// resolved to something public at save time and to an internal address afterwards — a DNS
    /// record the tenant controls — turned <c>POST /org/smtp/test</c> into a connect probe against
    /// the cluster, and every outbound mail presented the org's SMTP credentials to whatever
    /// answered. <c>WebhookService</c> re-validates at delivery for exactly this reason.
    /// </para>
    ///
    /// <para>
    /// The row is written straight to the database, which is the only way to model "valid when
    /// saved, hostile when used". The real SMTP client is used because the default fixture has no
    /// relay configured and answers OK without dialling anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task OrgSmtp_HostIsRevalidatedAtSendTime()
    {
        var (client, factory) = fixture.CreateRealSmtpClient();
        await using var _f = factory;

        var (org, list) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();

        fixture.Db.OrgSmtpConfigs.Add(new OrgSmtpConfig
        {
            Id          = Guid.NewGuid(),
            OrgId       = org.Id,
            Host        = "169.254.169.254",   // link-local: the cloud metadata address
            Port        = 587,
            StartTls    = true,
            FromAddress = "noreply@tenant.test",
            FromName    = "Tenant",
            CreatedAt   = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", fixture.Seed.OrgAdminToken(admin.Id, org.Id));

        var res = await client.PostAsync("/org/smtp/test", null);

        res.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "a host the validator would refuse must not be dialled just because it is already stored");
    }

    /// <summary>
    /// A revocation that failed must not be reported as one that succeeded.
    ///
    /// <para>
    /// <c>HydraService.RevokeSessionsAsync</c> and its two siblings threw the response away, so
    /// <c>PATCH /account/password</c> answered <c>{"sessions_revoked": true}</c> whether or not
    /// Hydra had done anything — and the <c>catch</c> beside the call could never fire, because
    /// nothing was thrown. Every stolen session stayed live while the API said otherwise. The same
    /// shape covered three Keto tuple deletes, which is what "tuple-first fails closed" depended on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PasswordChange_WhenSessionRevocationFails_DoesNotClaimSessionsWereRevoked()
    {
        var (org, list) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(list.Id);
        fixture.Keto.AllowAll();
        fixture.Hydra.SetupSessionRevocationFailure($"{org.Id}:{user.Id}");

        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(user.Id, org.Id));
        var res = await client.PatchAsJsonAsync("/account/password", new
        {
            current_password = "P@ssw0rd!Test",
            new_password     = "An0ther!Passw0rd",
        });

        if (res.StatusCode == HttpStatusCode.OK)
        {
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            if (body.TryGetProperty("sessions_revoked", out var revoked))
            {
                revoked.GetBoolean().Should().BeFalse(
                    "Hydra refused the revocation, so the API must not report the sessions as gone");
            }
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
