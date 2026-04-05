using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Webhooks;

/// <summary>
/// B5: Webhook CRUD, security, and event validation tests.
/// Note: actual HTTP delivery is fire-and-forget so tests only validate
/// the management API, not live delivery to external endpoints.
/// </summary>
[Collection("RediensIAM")]
public class WebhookTests(TestFixture fixture)
{
    // ── Org webhooks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateWebhook_ValidRequest_Returns201WithSecretShownOnce()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url    = "https://example.com/hook",
            events = new[] { "user.created", "user.login.success" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("secret").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("message").GetString().Should().Be("store_secret_shown_once");
    }

    [Fact]
    public async Task CreateWebhook_HttpUrl_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url    = "http://example.com/hook",
            events = new[] { "user.created" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("url_must_be_https");
    }

    [Fact]
    public async Task CreateWebhook_InvalidEvent_Returns400()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url    = "https://example.com/hook",
            events = new[] { "not.a.real.event" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_events");
    }

    [Fact]
    public async Task ListWebhooks_ReturnsOnlyOwnOrgWebhooks()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var (org2, orgList2) = await fixture.Seed.CreateOrgAsync();

        // Create webhook for org1
        var admin1 = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token1 = fixture.Seed.OrgAdminToken(admin1.Id, org.Id);
        fixture.Keto.AllowAll();
        await fixture.ClientWithToken(token1).PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://org1.example.com/hook", events = new[] { "user.created" }
        });

        // Create webhook for org2
        var admin2 = await fixture.Seed.CreateUserAsync(orgList2.Id);
        var token2 = fixture.Seed.OrgAdminToken(admin2.Id, org2.Id);
        await fixture.ClientWithToken(token2).PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://org2.example.com/hook", events = new[] { "user.created" }
        });

        var listRes = await fixture.ClientWithToken(token1).GetAsync("/org/webhooks");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var webhooks = await listRes.Content.ReadFromJsonAsync<JsonElement[]>();

        webhooks!.Should().AllSatisfy(w =>
            w.GetProperty("url").GetString().Should().Contain("org1"));
    }

    [Fact]
    public async Task GetWebhook_OtherOrg_Returns404()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var (org2, orgList2) = await fixture.Seed.CreateOrgAsync();

        var admin1 = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token1 = fixture.Seed.OrgAdminToken(admin1.Id, org.Id);
        fixture.Keto.AllowAll();
        var createRes = await fixture.ClientWithToken(token1).PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://org1.example.com/hook", events = new[] { "user.created" }
        });
        var webhookId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        // org2 admin tries to read org1's webhook
        var admin2 = await fixture.Seed.CreateUserAsync(orgList2.Id);
        var token2 = fixture.Seed.OrgAdminToken(admin2.Id, org2.Id);
        var res = await fixture.ClientWithToken(token2).GetAsync($"/org/webhooks/{webhookId}");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteWebhook_Owner_Returns204()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });
        var webhookId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var res = await client.DeleteAsync($"/org/webhooks/{webhookId}");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getRes = await client.GetAsync($"/org/webhooks/{webhookId}");
        getRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RotateSecret_Returns200WithNewSecret()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });
        var created = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var webhookId = created.GetProperty("id").GetString();
        var originalSecret = created.GetProperty("secret").GetString();

        var rotateRes = await client.PostAsJsonAsync($"/org/webhooks/{webhookId}/rotate-secret", new { });
        rotateRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotateBody = await rotateRes.Content.ReadFromJsonAsync<JsonElement>();
        var newSecret = rotateBody.GetProperty("secret").GetString();

        newSecret.Should().NotBeNullOrEmpty();
        newSecret.Should().NotBe(originalSecret);
    }

    [Fact]
    public async Task TestWebhook_Returns200()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });
        var webhookId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var res = await client.PostAsJsonAsync($"/org/webhooks/{webhookId}/test", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateWebhook_ChangeActiveAndEvents_Persists()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });
        var webhookId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        var patchRes = await client.PatchAsJsonAsync($"/org/webhooks/{webhookId}", new
        {
            active = false, events = new[] { "user.login.success", "user.login.failure" }
        });
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        await fixture.RefreshDbAsync();
        var updated = await fixture.Db.Webhooks.FindAsync(Guid.Parse(webhookId!));
        updated!.Active.Should().BeFalse();
        updated.Events.Should().Contain("user.login.success");
        updated.Events.Should().NotContain("user.created");
    }

    // ── Admin webhooks ────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminCreateWebhook_SuperAdmin_Returns201()
    {
        var (_, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/admin/webhooks", new
        {
            url    = "https://admin.example.com/hook",
            events = new[] { "user.created", "project.updated" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("secret").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AdminCreateWebhook_OrgAdmin_Returns403()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/admin/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── B5: Secret rotation — HMAC verification ───────────────────────────────

    /// <summary>
    /// After rotation the new secret produces a different HMAC for the same payload,
    /// confirming old signatures would no longer pass verification.
    /// </summary>
    [Fact]
    public async Task RotateSecret_OldSecretProducesDifferentHmac()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });
        var created       = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var webhookId     = created.GetProperty("id").GetString()!;
        var originalSecret = created.GetProperty("secret").GetString()!;

        var rotateRes = await client.PostAsJsonAsync($"/org/webhooks/{webhookId}/rotate-secret", new { });
        rotateRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var newSecret = (await rotateRes.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("secret").GetString()!;

        newSecret.Should().NotBe(originalSecret);

        // Compute HMAC-SHA256 for the same payload using each secret
        var payload = global::System.Text.Encoding.UTF8.GetBytes("""{"event":"user.created"}""");

        static string Hmac(string secret, byte[] data)
        {
            var key  = global::System.Text.Encoding.UTF8.GetBytes(secret);
            using var hmac = new global::System.Security.Cryptography.HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(data));
        }

        var sigWithOld = Hmac(originalSecret, payload);
        var sigWithNew = Hmac(newSecret, payload);
        sigWithOld.Should().NotBe(sigWithNew,
            "HMAC computed with old secret must differ from one computed with new secret");
    }

    /// <summary>
    /// After rotation the webhook's secret hash is updated in the database.
    /// </summary>
    [Fact]
    public async Task RotateSecret_SecretEncUpdatedInDb()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url = "https://example.com/hook", events = new[] { "user.created" }
        });
        var created   = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var webhookId = Guid.Parse(created.GetProperty("id").GetString()!);

        await fixture.RefreshDbAsync();
        var before = (await fixture.Db.Webhooks.FindAsync(webhookId))!.SecretEnc;

        await client.PostAsJsonAsync($"/org/webhooks/{webhookId}/rotate-secret", new { });

        await fixture.RefreshDbAsync();
        var after = (await fixture.Db.Webhooks.FindAsync(webhookId))!.SecretEnc;

        after.Should().NotBe(before, "SecretEnc must be updated after rotation");
    }

    // ── Supported events list ─────────────────────────────────────────────────

    [Fact]
    public void SupportedEvents_ContainsExpectedEvents()
    {
        WebhookEvents.All.Should().Contain("user.created");
        WebhookEvents.All.Should().Contain("user.login.success");
        WebhookEvents.All.Should().Contain("user.login.failure");
        WebhookEvents.All.Should().Contain("role.assigned");
        WebhookEvents.All.Should().Contain("project.updated");
    }
}
