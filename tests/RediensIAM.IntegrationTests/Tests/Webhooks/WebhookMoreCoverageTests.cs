using System.Net.Http.Json;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Webhooks;

/// <summary>
/// Covers WebhookController lines not yet hit by existing tests:
///   - GET /org/webhooks/{id}       — recent_deliveries lambda body (lines 83-86)
///   - PATCH /org/webhooks/{id}     — valid https URL update (lines 100-101)
///   - POST /admin/webhooks         — http URL rejected (line 188)
///   - POST /admin/webhooks         — invalid events rejected (line 192)
/// </summary>
[Collection("RediensIAM")]
public class WebhookMoreCoverageTests(TestFixture fixture)
{
    private async Task<(HttpClient client, Guid webhookId, Guid orgId)> CreateOrgWebhookAsync()
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
        var body      = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var webhookId = Guid.Parse(body.GetProperty("id").GetString()!);
        return (client, webhookId, org.Id);
    }

    private async Task<(HttpClient client, Guid webhookId)> CreateAdminWebhookClientAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.SuperAdminToken(admin.Id);
        fixture.Keto.AllowAll();
        return (fixture.ClientWithToken(token), Guid.Empty);
    }

    // ── GET /org/webhooks/{id} — recent_deliveries lambda body (lines 83-86) ──

    [Fact]
    public async Task GetWebhook_WithDeliveries_ReturnsRecentDeliveries()
    {
        var (client, webhookId, _) = await CreateOrgWebhookAsync();

        // Seed a delivery so the LINQ Select body (lines 83-86) is exercised
        fixture.Db.WebhookDeliveries.Add(new WebhookDelivery
        {
            Id           = Guid.NewGuid(),
            WebhookId    = webhookId,
            Event        = "user.created",
            Payload      = "{}",
            StatusCode   = 200,
            AttemptCount = 1,
            DeliveredAt  = DateTimeOffset.UtcNow,
            CreatedAt    = DateTimeOffset.UtcNow,
        });
        await fixture.Db.SaveChangesAsync();

        var res = await client.GetAsync($"/org/webhooks/{webhookId}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("recent_deliveries").GetArrayLength().Should().BeGreaterThan(0);
    }

    // ── PATCH /org/webhooks/{id} — valid https URL (lines 100-101) ────────────

    [Fact]
    public async Task UpdateWebhook_ValidHttpsUrl_Returns200()
    {
        var (client, webhookId, _) = await CreateOrgWebhookAsync();

        var res = await client.PatchAsJsonAsync($"/org/webhooks/{webhookId}", new
        {
            url = "https://updated.example.com/hook"
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── POST /admin/webhooks — http URL rejected (line 188) ───────────────────

    [Fact]
    public async Task AdminCreateWebhook_HttpUrl_Returns400()
    {
        var (client, _) = await CreateAdminWebhookClientAsync();

        var res = await client.PostAsJsonAsync("/admin/webhooks", new
        {
            url    = "http://not-https.example.com/hook",
            events = new[] { "user.created" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("url_must_be_https");
    }

    // ── POST /admin/webhooks — invalid events (line 192) ─────────────────────

    [Fact]
    public async Task AdminCreateWebhook_InvalidEvents_Returns400()
    {
        var (client, _) = await CreateAdminWebhookClientAsync();

        var res = await client.PostAsJsonAsync("/admin/webhooks", new
        {
            url    = "https://example.com/hook",
            events = new[] { "not.a.real.event" }
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_events");
    }
}
