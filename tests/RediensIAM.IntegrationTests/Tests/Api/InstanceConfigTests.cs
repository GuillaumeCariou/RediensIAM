using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Api;

/// <summary>
/// <c>GET</c> / <c>PATCH /admin/instance</c> — the write half the instance row never had.
///
/// <para>
/// Two properties carry the weight here. A setting must mean the same thing whether an operator
/// types it or a manifest declares it, which is why every value goes through the same
/// <c>AppConfig.Clamp*</c> as the environment path. And the settings that are <b>not</b> writable
/// must stay that way: Argon costs drive the pod's memory limit, and the trust anchors are the one
/// thing a process must not learn from data it can write.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class InstanceConfigTests(TestFixture fixture)
{
    private async Task<HttpClient> SuperAdminAsync()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        return fixture.ClientWithToken(fixture.Seed.SuperAdminToken(user.Id));
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsTheSettingsInForce()
    {
        var client = await SuperAdminAsync();

        var body = await BodyOf(await client.GetAsync("/admin/instance"));

        body.GetProperty("settings").GetProperty("max_login_attempts").GetInt32().Should().BeGreaterThan(0);
        body.GetProperty("config_version").GetInt64().Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The settings this endpoint will never change are shown rather than hidden: an operator
    /// asking "why is this not what I set" needs to see which values do not come from here.
    /// </summary>
    [Fact]
    public async Task Get_AlsoShowsWhatOnlyTheEnvironmentDecides()
    {
        var client = await SuperAdminAsync();

        var body = await BodyOf(await client.GetAsync("/admin/instance"));
        var envOnly = body.GetProperty("environment_only");

        envOnly.TryGetProperty("argon_memory_cost", out _).Should().BeTrue();
        envOnly.TryGetProperty("trusted_proxies", out _).Should().BeTrue();
        envOnly.TryGetProperty("hydra_admin_url", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Get_IsRefusedBelowSuperAdmin()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var user = await fixture.Seed.CreateUserAsync(orgList.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(fixture.Seed.OrgAdminToken(user.Id, org.Id));

        var res = await client.GetAsync("/admin/instance");

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_StoresWhatItWasGiven()
    {
        var client = await SuperAdminAsync();

        var body = await BodyOf(await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = 42 }));

        body.GetProperty("changed").EnumerateArray().Select(x => x.GetString()).Should().Contain("invite_expiry_hours");
        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("settings").GetProperty("invite_expiry_hours").GetInt32().Should().Be(42);
    }

    /// <summary>
    /// A PATCH states what changes. A body naming one setting must not reset the nineteen it does
    /// not name — the failure mode of a PUT wearing a PATCH's name.
    /// </summary>
    [Fact]
    public async Task Patch_LeavesUnnamedSettingsAlone()
    {
        var client = await SuperAdminAsync();
        await client.PatchAsJsonAsync("/admin/instance", new { audit_retention_days = 200 });

        await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = 33 });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("settings").GetProperty("audit_retention_days").GetInt32().Should().Be(200);
        after.GetProperty("settings").GetProperty("invite_expiry_hours").GetInt32().Should().Be(33);
    }

    /// <summary>
    /// Out of range is clamped, not refused. The request states an intent — "make lockout longer" —
    /// and a 400 on 100000 teaches the operator nothing that storing the ceiling does not. What
    /// matters is that the stored value is the clamped one, and that the answer says so.
    /// </summary>
    [Fact]
    public async Task Patch_ClampsRatherThanRefuses()
    {
        var client = await SuperAdminAsync();

        await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = 100_000, audit_retention_days = 1 });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("settings").GetProperty("invite_expiry_hours").GetInt32()
            .Should().Be(AppConfig.ClampInviteExpiryHours(100_000));
        after.GetProperty("settings").GetProperty("audit_retention_days").GetInt32()
            .Should().Be(AppConfig.ClampRetention(1));
    }

    /// <summary>
    /// The same bound on both paths. If the endpoint clamped differently from the environment, a
    /// value would mean one thing typed and another declared — which is the defect this whole
    /// surface exists inside of.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(721)]
    [InlineData(int.MaxValue)]
    public async Task Patch_UsesTheSameBoundsAsTheEnvironment(int requested)
    {
        var client = await SuperAdminAsync();

        await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = requested });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("settings").GetProperty("invite_expiry_hours").GetInt32()
            .Should().Be(AppConfig.ClampInviteExpiryHours(requested));
    }

    [Fact]
    public async Task Patch_BumpsTheVersionOnlyWhenSomethingChanged()
    {
        var client = await SuperAdminAsync();
        var before = (await BodyOf(await client.GetAsync("/admin/instance"))).GetProperty("config_version").GetInt64();

        await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = 44 });
        var afterChange = (await BodyOf(await client.GetAsync("/admin/instance"))).GetProperty("config_version").GetInt64();
        afterChange.Should().BeGreaterThan(before);

        // The same value again is not a change, and a version that moves on a no-op is a version
        // other replicas reload for nothing.
        await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = 44 });
        var afterNoop = (await BodyOf(await client.GetAsync("/admin/instance"))).GetProperty("config_version").GetInt64();
        afterNoop.Should().Be(afterChange);
    }

    /// <summary>
    /// The answer separates what is <b>stored</b> from what is <b>in force</b>, and this asserts
    /// the case where they differ.
    ///
    /// <para>
    /// A configuration source added after the instance provider wins over the row. That is real:
    /// this very test harness pins <c>Security:LockoutMinutes</c>, and a deployment can do the same
    /// with an environment variable. An operator who writes a setting and sees it "not apply" is
    /// looking at exactly this, and the endpoint has to show it rather than let them conclude the
    /// write was lost. Reporting only one number would make that indistinguishable from a bug.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Get_ShowsStoredAndInForceSeparatelyWhenTheEnvironmentOverrides()
    {
        var client = await SuperAdminAsync();

        await client.PatchAsJsonAsync("/admin/instance", new { lockout_minutes = 99 });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("stored").GetProperty("lockout_minutes").GetInt32()
            .Should().Be(99, "the row is what the write reached");
        after.GetProperty("settings").GetProperty("lockout_minutes").GetInt32()
            .Should().NotBe(99, "a source added after the instance provider still wins, and saying so is the point");
    }

    /// <summary>
    /// A console write survives the environment being re-read.
    ///
    /// <para>
    /// The instance provider re-applies the environment on every load, deliberately: a row frozen
    /// at first boot was a real defect, and an operator editing the chart has to see it take
    /// effect. Applied unconditionally, that also erased anything written here — so this endpoint
    /// would have been a control that does not hold, which is worse than no control. The provider
    /// now applies the keys whose environment value <b>changed</b>, and this is the assertion that
    /// keeps the two behaviours from trading places again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Patch_SurvivesTheEnvironmentBeingReApplied()
    {
        var client = await SuperAdminAsync();

        // The PATCH itself reloads the configuration, which is when ApplyEnv used to overwrite it.
        await client.PatchAsJsonAsync("/admin/instance", new { lockout_minutes = 97, audit_retention_days = 321 });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("stored").GetProperty("lockout_minutes").GetInt32().Should().Be(97,
            "the environment pins this key, and re-applying it unchanged must not undo an operator's write");
        after.GetProperty("stored").GetProperty("audit_retention_days").GetInt32().Should().Be(321);
    }

    // ── What it must never write ──────────────────────────────────────────────

    /// <summary>
    /// Argon costs drive the pod's memory limit: raising them from a browser kills the process that
    /// served the request. The field is not in the request record at all, so the binder discards it
    /// — asserted here because "not declared" is only a guarantee while nobody declares it.
    /// </summary>
    [Fact]
    public async Task Patch_CannotChangeTheArgonCosts()
    {
        var client = await SuperAdminAsync();
        var before = await BodyOf(await client.GetAsync("/admin/instance"));
        var memoryBefore = before.GetProperty("environment_only").GetProperty("argon_memory_cost").GetInt32();

        await client.PatchAsJsonAsync("/admin/instance", new { argon_memory_cost = 8, argon_time_cost = 1 });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("environment_only").GetProperty("argon_memory_cost").GetInt32().Should().Be(memoryBefore);
    }

    /// <summary>A process must not learn who to trust from a row it can write.</summary>
    [Fact]
    public async Task Patch_CannotChangeTheTrustAnchors()
    {
        var client = await SuperAdminAsync();
        var before = await BodyOf(await client.GetAsync("/admin/instance"));
        var hydraBefore = before.GetProperty("environment_only").GetProperty("hydra_admin_url").GetString();

        await client.PatchAsJsonAsync("/admin/instance", new
        {
            hydra_admin_url = "http://attacker.example", trusted_proxies = "0.0.0.0/0",
        });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("environment_only").GetProperty("hydra_admin_url").GetString().Should().Be(hydraBefore);
        after.GetProperty("environment_only").GetProperty("trusted_proxies").GetString().Should().NotBe("0.0.0.0/0");
    }

    /// <summary>
    /// Topology decides where the OAuth2 client the console is authenticated through points. A
    /// console that could rewrite it could lock every administrator out, including itself.
    /// </summary>
    [Fact]
    public async Task Patch_CannotChangeTheTopology()
    {
        var client = await SuperAdminAsync();
        var before = await BodyOf(await client.GetAsync("/admin/instance"));
        var urlBefore = before.GetProperty("environment_only").GetProperty("public_url").GetString();

        await client.PatchAsJsonAsync("/admin/instance", new { public_url = "http://elsewhere.example" });

        var after = await BodyOf(await client.GetAsync("/admin/instance"));
        after.GetProperty("environment_only").GetProperty("public_url").GetString().Should().Be(urlBefore);
    }

    // ── Audit ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_IsAuditedWithWhatChanged()
    {
        var client = await SuperAdminAsync();

        await client.PatchAsJsonAsync("/admin/instance", new { otp_ttl_seconds = 120 });

        var row = await fixture.Db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "instance.updated")
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();

        row.Should().NotBeNull();
        row!.Metadata.Should().ContainKey("otp_ttl_seconds");
    }

    // ── It actually takes effect ──────────────────────────────────────────────

    /// <summary>
    /// The point of the whole surface: a setting written here is in force on this pod without a
    /// restart. `AppConfig` reads through `IConfiguration` on every access and the instance row is
    /// a provider, so the write is followed by a reload — and this is what proves the chain rather
    /// than assuming it.
    /// </summary>
    [Fact]
    public async Task Patch_TakesEffectWithoutARestart()
    {
        var client = await SuperAdminAsync();

        await client.PatchAsJsonAsync("/admin/instance", new { invite_expiry_hours = 5 });

        fixture.GetService<AppConfig>().InviteExpiryHours.Should().Be(5);
    }
}
