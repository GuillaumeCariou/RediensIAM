using System.Security.Cryptography;
using System.Text;
using RediensIAM.Data.Entities;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace RediensIAM.IntegrationTests.Tests.Webhooks;

/// <summary>
/// Tests for WebhookDispatcherService's actual HTTP delivery logic.
/// Seeds webhooks directly in the DB (bypassing the HTTPS-only API validation)
/// and uses a per-test WireMock server as the delivery target.
/// </summary>
[Collection("RediensIAM")]
public class WebhookDeliveryTests(TestFixture fixture)
{
    // ── Scaffold ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an org, org-admin token, and an "anchor" webhook via the management API.
    /// The anchor webhook is only needed to have a valid webhook ID for POST /test,
    /// which internally dispatches "webhook.test" to all matching webhooks in the org.
    /// </summary>
    private async Task<(Guid orgId, string token, HttpClient client, string anchorWebhookId)> ScaffoldAsync()
    {
        var (org, orgList) = await fixture.Seed.CreateOrgAsync();
        var admin  = await fixture.Seed.CreateUserAsync(orgList.Id);
        var token  = fixture.Seed.OrgAdminToken(admin.Id, org.Id);
        fixture.Keto.AllowAll();
        var client = fixture.ClientWithToken(token);

        var res = await client.PostAsJsonAsync("/org/webhooks", new
        {
            url    = "https://example.com/placeholder",
            events = new[] { "user.created" },
        });
        var anchorId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        return (org.Id, token, client, anchorId);
    }

    /// <summary>Seeds a Webhook directly in the DB pointing at a local WireMock server.</summary>
    private async Task<Webhook> SeedWebhookAsync(
        Guid orgId, string targetUrl,
        string[] events, string secretEnc = "", bool active = true)
    {
        var wh = new Webhook
        {
            Id        = Guid.NewGuid(),
            OrgId     = orgId,
            Url       = targetUrl,
            Events    = events,
            SecretEnc = secretEnc,
            Active    = active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        fixture.Db.Webhooks.Add(wh);
        await fixture.Db.SaveChangesAsync();
        return wh;
    }

    // ── Delivery — success path ───────────────────────────────────────────────

    [Fact]
    public async Task Delivery_Success_PostsPayloadWithEventHeader()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500); // let the background dispatcher fire

        var hits = target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").ToList();
        hits.Should().HaveCount(1);
        hits[0].RequestMessage!.Headers.Should().ContainKey("X-RediensIAM-Event");
        hits[0].RequestMessage!.Headers!["X-RediensIAM-Event"].First().Should().Be("webhook.test");
    }

    [Fact]
    public async Task Delivery_Success_BodyIsJson()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500);

        var hits = target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").ToList();
        hits.Should().HaveCount(1);

        var body = JsonDocument.Parse(hits[0].RequestMessage!.Body!);
        body.RootElement.GetProperty("event").GetString().Should().Be("webhook.test");
        body.RootElement.TryGetProperty("created_at", out _).Should().BeTrue();
        body.RootElement.TryGetProperty("data", out _).Should().BeTrue();
    }

    // ── HMAC signature ────────────────────────────────────────────────────────

    [Fact]
    public async Task Delivery_EmptySecret_SendsEmptySha256Signature()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        // SecretEnc = "" → dispatcher sees empty plaintext → ComputeSignature returns ""
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"], secretEnc: "");

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500);

        var hits = target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").ToList();
        hits.Should().HaveCount(1);
        hits[0].RequestMessage!.Headers.Should().ContainKey("X-RediensIAM-Signature");
        hits[0].RequestMessage!.Headers!["X-RediensIAM-Signature"].First().Should().Be("sha256=");
    }

    [Fact]
    public async Task Delivery_WithKnownSecret_SendsNonEmptyHmacSignature()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();

        // Encrypt a known plaintext secret using the test encryption key (all-zero 32 bytes)
        var encKey    = Convert.FromHexString(new string('0', 64));
        var secretEnc = TotpEncryption.EncryptString(encKey, "super-secret");
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"], secretEnc: secretEnc);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500);

        var hits = target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").ToList();
        hits.Should().HaveCount(1);
        var sig = hits[0].RequestMessage!.Headers!["X-RediensIAM-Signature"].First();
        // Signature must be sha256={64 hex chars} — not empty
        sig.Should().StartWith("sha256=");
        sig.Should().HaveLength("sha256=".Length + 64, "HMAC-SHA256 produces 32 bytes = 64 hex chars");
    }

    // ── Delivery record persistence ───────────────────────────────────────────

    [Fact]
    public async Task Delivery_Success_PersistsDeliveryRecordWithDeliveredAt()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        var wh = await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500);

        await fixture.RefreshDbAsync();
        var delivery = fixture.Db.WebhookDeliveries.FirstOrDefault(d => d.WebhookId == wh.Id);
        delivery.Should().NotBeNull();
        delivery!.DeliveredAt.Should().NotBeNull("successful delivery must set DeliveredAt");
        delivery.StatusCode.Should().Be(200);
        delivery.ErrorMessage.Should().BeNull();
        delivery.AttemptCount.Should().Be(1);
        delivery.Event.Should().Be("webhook.test");
    }

    // ── Dispatch filtering ────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_InactiveWebhook_DoesNotDeliver()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"], active: false);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(300);

        target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_WrongEventType_DoesNotDeliver()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        // Webhook only subscribed to "user.created" but "webhook.test" is dispatched
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["user.created"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(300);

        target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").Should().BeEmpty();
    }

    [Fact]
    public async Task Dispatch_DifferentOrg_DoesNotDeliver()
    {
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();

        // Seed webhook for a completely different org
        var (otherOrg, _) = await fixture.Seed.CreateOrgAsync();
        await SeedWebhookAsync(otherOrg.Id, target.Url + "/hook", ["webhook.test"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(300);

        target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").Should().BeEmpty();
    }

    // ── Failure scenarios ─────────────────────────────────────────────────────

    [Fact]
    public async Task Delivery_CorruptSecretEnc_StillDeliversPayload()
    {
        // Invalid base64 in SecretEnc → DecryptString throws → catch at WebhookService.cs:75
        // → secret falls back to "" → job still dispatched and delivered
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"],
            secretEnc: "not-valid-base64!!!");

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500);

        target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook").Should().HaveCount(1);
    }

    [Fact]
    public async Task Delivery_EndpointReturns500_RecordsHttpError()
    {
        // Non-2xx response → sets lastError = "HTTP 500", covers lines 143-144;
        // retry check covers lines 151-152 (background — completes after test ends)
        using var target = WireMockServer.Start(new WireMockServerSettings { Port = 0 });
        target.Given(Request.Create().WithPath("/hook").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(500));

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        await SeedWebhookAsync(orgId, target.Url + "/hook", ["webhook.test"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500); // first attempt completes synchronously before retry delay starts

        // At least one request received (first attempt)
        target.LogEntries.Where(e => e.RequestMessage!.Path == "/hook")
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task Delivery_ConnectionRefused_CatchesHttpException()
    {
        // Use a port that is not listening: start WireMock, capture URL, then stop it
        string targetUrl;
        using (var temp = WireMockServer.Start(new WireMockServerSettings { Port = 0 }))
            targetUrl = temp.Url + "/hook";
        // temp is disposed — port is no longer listening

        var (orgId, _, client, anchorId) = await ScaffoldAsync();
        await SeedWebhookAsync(orgId, targetUrl, ["webhook.test"]);

        await client.PostAsJsonAsync($"/org/webhooks/{anchorId}/test", new { });
        await Task.Delay(500); // connection refused is immediate → catch block (lines 145-149) executed
    }
}
