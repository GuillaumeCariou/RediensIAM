using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Services;

// ── Supported webhook event types ─────────────────────────────────────────────

public static class WebhookEvents
{
    public static readonly string[] All =
    [
        "user.created", "user.updated", "user.deleted", "user.locked", "user.unlocked",
        "user.login.success", "user.login.failure",
        "role.assigned", "role.revoked",
        "session.revoked",
        "project.updated",
        "invite.sent",
    ];
}

// ── Channel job ───────────────────────────────────────────────────────────────

public sealed record WebhookJob(
    Guid WebhookId,
    string EventType,
    string Payload,
    string SecretPlain,
    string Url);

// ── WebhookService — enqueues jobs, used by other services ───────────────────

public class WebhookService(
    RediensIamDbContext db,
    AppConfig appConfig,
    Channel<WebhookJob> channel)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task DispatchAsync(
        string eventType,
        object payloadObj,
        Guid? orgId,
        Guid? projectId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            @event = eventType,
            created_at = DateTimeOffset.UtcNow,
            data = payloadObj
        }, JsonOpts);

        var webhooks = await db.Webhooks
            .Where(w => w.Active
                && w.Events.Contains(eventType)
                && (w.OrgId == orgId || w.OrgId == null)
                && (w.ProjectId == projectId || w.ProjectId == null))
            .ToListAsync();

        var encKey = Convert.FromHexString(appConfig.TotpSecretEncryptionKey);

        foreach (var wh in webhooks)
        {
            var secret = "";
            if (!string.IsNullOrEmpty(wh.SecretEnc))
            {
                try { secret = TotpEncryption.DecryptString(encKey, wh.SecretEnc); }
                catch { /* corrupt key — still deliver, just without a valid signature */ }
            }

            var job = new WebhookJob(wh.Id, eventType, payload, secret, wh.Url);
            await channel.Writer.WriteAsync(job);
        }
    }

    // Called by the dispatcher to log the attempt result
    public async Task RecordDeliveryAsync(WebhookDelivery delivery)
    {
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync();
    }
}

// ── WebhookDispatcherService — background worker that sends HTTP payloads ───

public class WebhookDispatcherService(
    Channel<WebhookJob> channel,
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookDispatcherService> logger,
    IHttpClientFactory httpClientFactory,
    AppConfig appConfig) : BackgroundService
{
    // Retry delays: 2s, 8s, 32s
    private static readonly int[] RetryDelaysMs = [2_000, 8_000, 32_000];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in channel.Reader.ReadAllAsync(stoppingToken))
        {
            _ = Task.Run(() => ProcessJobAsync(job, stoppingToken), stoppingToken);
        }
    }

    private async Task ProcessJobAsync(WebhookJob job, CancellationToken ct)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(job.Payload);
        var sig = ComputeSignature(job.SecretPlain, payloadBytes);

        int? lastStatus = null;
        string? lastError = null;
        var delivered = false;
        var attempts  = 0;

        for (var i = 0; i <= RetryDelaysMs.Length; i++)
        {
            attempts++;
            try
            {
                using var client = httpClientFactory.CreateClient("webhook");
                client.Timeout = TimeSpan.FromSeconds(appConfig.WebhookTimeoutSeconds);

                using var req = new HttpRequestMessage(HttpMethod.Post, job.Url);
                req.Content = new ByteArrayContent(payloadBytes);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                req.Headers.Add("X-RediensIAM-Signature", $"sha256={sig}");
                req.Headers.Add("X-RediensIAM-Event", job.EventType);

                var resp = await client.SendAsync(req, ct);
                lastStatus = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    delivered = true;
                    break;
                }
                lastError = $"HTTP {lastStatus}";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex.Message;
                logger.LogWarning("Webhook {Id} attempt {Attempt} failed: {Error}", job.WebhookId, attempts, ex.Message);
            }

            if (i < RetryDelaysMs.Length)
                await Task.Delay(RetryDelaysMs[i], ct);
        }

        var delivery = new WebhookDelivery
        {
            Id           = Guid.NewGuid(),
            WebhookId    = job.WebhookId,
            Event        = job.EventType,
            Payload      = job.Payload,
            StatusCode   = lastStatus,
            ErrorMessage = delivered ? null : lastError,
            AttemptCount = attempts,
            DeliveredAt  = delivered ? DateTimeOffset.UtcNow : null,
            CreatedAt    = DateTimeOffset.UtcNow
        };

        try
        {
            using var scope = scopeFactory.CreateScope();
            var webhookService = scope.ServiceProvider.GetRequiredService<WebhookService>();
            await webhookService.RecordDeliveryAsync(delivery);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist webhook delivery record for {Id}", job.WebhookId);
        }
    }

    private static string ComputeSignature(string secret, byte[] payload)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }
}
