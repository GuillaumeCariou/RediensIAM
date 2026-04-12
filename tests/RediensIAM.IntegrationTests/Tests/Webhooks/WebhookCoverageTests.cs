using System.Net.Http.Json;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Webhooks;

/// <summary>
/// Targets specific uncovered lines in WebhookController identified via SonarQube:
///   - GetWebhook success path (lines 80-87)
///   - UpdateWebhook with HTTP URL validation (lines 97-98)
///   - ListDeliveries endpoint (lines 148-157)
///   - AdminListWebhooks (lines 177-181)
///   - AdminDeleteWebhook (lines 219-226)
/// </summary>
[Collection("RediensIAM")]
public class WebhookCoverageTests(TestFixture fixture)
{
    private async Task<(HttpClient client, Guid webhookId)> CreateOrgWebhookAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url    = "https://example.com/hook",
            events = new[] { "user.created" }
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var body      = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var webhookId = Guid.Parse(body.GetProperty("id").GetString()!);
        return (client, webhookId);
    }

    private async Task<(HttpClient client, Guid webhookId)> CreateAdminWebhookAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var createRes = await client.PostAsJsonAsync("/admin/webhooks", new
        {
            url    = "https://admin-hook.example.com/events",
            events = new[] { "user.created" }
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var body      = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var webhookId = Guid.Parse(body.GetProperty("id").GetString()!);
        return (client, webhookId);
    }

    // ── GET /org/webhooks/{id} — success path (lines 80-87) ──────────────────

    [Fact]
    public async Task GetWebhook_ExistingWebhook_Returns200WithDetails()
    {
        var (client, webhookId) = await CreateOrgWebhookAsync();

        var res = await client.GetAsync($"/org/webhooks/{webhookId}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("id").GetString().Should().Be(webhookId.ToString());
        body.TryGetProperty("recent_deliveries", out _).Should().BeTrue();
    }

    // ── PATCH /org/webhooks/{id} — HTTP URL rejected (lines 97-98) ───────────

    [Fact]
    public async Task UpdateWebhook_HttpUrl_Returns400()
    {
        var (client, webhookId) = await CreateOrgWebhookAsync();

        var res = await client.PatchAsJsonAsync($"/org/webhooks/{webhookId}", new
        {
            url = "http://not-https.example.com/hook"
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("url_must_be_https");
    }

    // ── GET /org/webhooks/{id}/deliveries — list deliveries (lines 148-157) ──

    [Fact]
    public async Task ListDeliveries_ExistingWebhook_Returns200WithArray()
    {
        var (client, webhookId) = await CreateOrgWebhookAsync();

        var res = await client.GetAsync($"/org/webhooks/{webhookId}/deliveries");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task ListDeliveries_NonExistentWebhook_Returns404()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.GetAsync($"/org/webhooks/{Guid.NewGuid()}/deliveries");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GET /admin/webhooks — list all webhooks (lines 177-181) ──────────────

    [Fact]
    public async Task AdminListWebhooks_SuperAdmin_Returns200WithArray()
    {
        var (client, _) = await CreateAdminWebhookAsync();

        var res = await client.GetAsync("/admin/webhooks");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    // ── DELETE /admin/webhooks/{id} (lines 219-226) ───────────────────────────

    [Fact]
    public async Task AdminDeleteWebhook_SuperAdmin_Returns204()
    {
        var (client, webhookId) = await CreateAdminWebhookAsync();

        var res = await client.DeleteAsync($"/admin/webhooks/{webhookId}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AdminDeleteWebhook_NonExistent_Returns404()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.DeleteAsync($"/admin/webhooks/{Guid.NewGuid()}");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
