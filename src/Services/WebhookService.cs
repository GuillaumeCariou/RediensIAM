using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Controllers;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using StackExchange.Redis;

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

// ── Redis queue abstraction (allows unit testing without IDatabase stub) ─────

public interface IWebhookQueue
{
    Task PersistAsync(string jobJson, long score);
    Task<string[]> RecoverAllAsync();
    Task RemoveAsync(string jobJson);
}

public sealed class RedisWebhookQueue(IConnectionMultiplexer redis) : IWebhookQueue
{
    public Task PersistAsync(string jobJson, long score)
        => redis.GetDatabase().SortedSetAddAsync(WebhookService.PendingKey, jobJson, score);

    public async Task<string[]> RecoverAllAsync()
    {
        var entries = await redis.GetDatabase().SortedSetRangeByScoreAsync(WebhookService.PendingKey);
        return entries.Select(e => e.ToString()).ToArray();
    }

    public Task RemoveAsync(string jobJson)
        => redis.GetDatabase().SortedSetRemoveAsync(WebhookService.PendingKey, jobJson);
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
    Channel<WebhookJob> channel,
    IWebhookQueue webhookQueue)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
        { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static readonly JsonSerializerOptions JobOpts = new();
    internal const string PendingKey = "webhook:pending";

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

        foreach (var wh in webhooks)
        {
            var secret = "";
            if (!string.IsNullOrEmpty(wh.SecretEnc))
            {
                try { secret = TotpEncryption.DecryptString(appConfig.WebhookEncKey, wh.SecretEnc); }
                catch { /* corrupt key — still deliver, just without a valid signature */ }
            }

            var job = new WebhookJob(wh.Id, eventType, payload, secret, wh.Url);
            var jobJson = JsonSerializer.Serialize(job, JobOpts);
            await webhookQueue.PersistAsync(jobJson, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
    AppConfig appConfig,
    IWebhookQueue webhookQueue,
    IWebhookSsrfValidator ssrfValidator) : BackgroundService
{
    // Retry delays: 2s, 8s, 32s
    private static readonly int[] RetryDelaysMs = [2_000, 8_000, 32_000];
    private readonly SemaphoreSlim _sem = new(20, 20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recover jobs that were pending when the pod last restarted
        var entries = await webhookQueue.RecoverAllAsync();
        var recovered = 0;
        foreach (var entry in entries)
        {
            var job = JsonSerializer.Deserialize<WebhookJob>(entry, WebhookService.JobOpts);
            if (job != null)
            {
                await channel.Writer.WriteAsync(job, stoppingToken);
                recovered++;
            }
        }
        if (recovered > 0 && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Recovered {Count} pending webhook jobs from Redis", recovered);

        await foreach (var job in channel.Reader.ReadAllAsync(stoppingToken))
        {
            var jobJson = JsonSerializer.Serialize(job, WebhookService.JobOpts);
            await _sem.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try { await ProcessJobAsync(job, jobJson, stoppingToken); }
                finally { _sem.Release(); }
            }, stoppingToken);
        }

        // Drain any buffered jobs after SIGTERM (best-effort, 10s window)
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (channel.Reader.TryRead(out var pending))
        {
            if (drainCts.IsCancellationRequested) break;
            var pendingJson = JsonSerializer.Serialize(pending, WebhookService.JobOpts);
            await _sem.WaitAsync(drainCts.Token).ConfigureAwait(false);
            _ = Task.Run(async () =>
            {
                try { await ProcessJobAsync(pending, pendingJson, drainCts.Token); }
                finally { _sem.Release(); }
            }, drainCts.Token);
        }
        // Wait for in-flight tasks to finish
        for (var i = 0; i < 20; i++)
        {
            if (_sem.CurrentCount == 20) break;
            await Task.Delay(500, CancellationToken.None);
        }
    }

    private async Task ProcessJobAsync(WebhookJob job, string jobJson, CancellationToken ct)
    {
        // Re-validate IP at delivery to prevent DNS rebinding (C8)
        if (await ssrfValidator.IsPrivateOrReservedAsync(job.Url))
        {
            logger.LogWarning("Webhook {Id} delivery blocked: URL resolved to private IP at delivery time", job.WebhookId);
            return;
        }

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
                logger.LogWarning(ex, "Webhook {Id} attempt {Attempt} failed: {Error}", job.WebhookId, attempts, ex.Message);
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

        // Remove from Redis persistent queue — job is done (delivered or exhausted)
        try { await webhookQueue.RemoveAsync(jobJson); }
        catch (Exception ex) { logger.LogWarning(ex, "Failed to remove webhook job from Redis queue"); }
    }

    private static string ComputeSignature(string secret, byte[] payload)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        return Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }
}
