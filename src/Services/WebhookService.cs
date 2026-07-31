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
    // Public webhook event names. These MUST stay stable for downstream subscribers.
    // Code in AuditLogService.RecordAsync sites is required to emit one of these strings
    // verbatim — otherwise the event is logged but no webhook fires.
    public static readonly string[] All =
    [
        // user lifecycle
        "user.created", "user.updated", "user.deleted", "user.unlocked",
        "user.registered", "user.registered.social",
        "user.invited", "user.invite_resent",
        // user auth
        "user.login.success", "user.login.failure", "user.login.locked", "user.login.saml",
        "user.password_changed", "user.password_reset_by_admin", "user.password.reset",
        "user.mfa.totp.failed", "user.mfa.sms.failed",
        // roles
        "role.created", "role.updated", "role.deleted",
        "role.assigned", "role.revoked",
        "role.management.assigned", "role.management.removed",
        // service accounts
        "sa.created", "sa.deleted",
        "sa.role.assigned", "sa.role.removed",
        // org / project
        "org.created", "org.suspended", "org.unsuspended", "org.settings_updated",
        "project.created", "project.updated", "project.deleted", "project.scopes_updated",
        // session / export
        "session.revoked",
        "export.users", "export.audit_log",
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
    IServiceScopeFactory scopeFactory,
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
        // Use a fresh DbContext scope: callers invoke this fire-and-forget from inside
        // a request, and the request-scoped DbContext is not thread-safe — sharing it
        // races with the caller's own awaits and triggers "second operation on context".
        var payload = JsonSerializer.Serialize(new
        {
            @event = eventType,
            created_at = DateTimeOffset.UtcNow,
            data = payloadObj
        }, JsonOpts);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
        var webhooks = await db.Webhooks
            .Where(w => w.Active
                && w.Events.Contains(eventType)
                && (w.OrgId == orgId || w.OrgId == null)
                && (w.ProjectId == projectId || w.ProjectId == null))
            .ToListAsync();

        foreach (var wh in webhooks)
            await EnqueueAsync(wh, eventType, payload);
    }

    /// <summary>
    /// Dispatches to one specific webhook, bypassing subscription matching.
    ///
    /// Used by the "send test" action: it emits <c>webhook.test</c>, which is deliberately not
    /// in <see cref="WebhookEvents.All"/> and therefore cannot be subscribed to. Routing the
    /// test through the normal <see cref="DispatchAsync"/> matching meant it silently matched
    /// nothing and the button was a no-op.
    /// </summary>
    public async Task DispatchToWebhookAsync(Guid webhookId, string eventType, object payloadObj)
    {
        var payload = JsonSerializer.Serialize(new
        {
            @event = eventType,
            created_at = DateTimeOffset.UtcNow,
            data = payloadObj
        }, JsonOpts);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
        var wh = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == webhookId && w.Active);
        if (wh == null) return;

        await EnqueueAsync(wh, eventType, payload);
    }

    private async Task EnqueueAsync(Webhook wh, string eventType, string payload)
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

    // Called by the dispatcher to log the attempt result. Uses its own scope to keep
    // DbContext usage off the request thread.
    public async Task RecordDeliveryAsync(WebhookDelivery delivery)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
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

        // Sign over "{timestamp}.{payload}" so a receiver can reject replays by checking the
        // age of the timestamp. Signing the payload alone made every delivery replayable
        // forever. ComputeSignature also has to be inside the try: a non-base64 secret threw
        // out of the un-awaited Task.Run and the job vanished silently.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string sig;
        try
        {
            sig = ComputeSignature(job.SecretPlain, timestamp, payloadBytes);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Webhook {Id}: stored secret is not valid base64 — delivering unsigned", job.WebhookId);
            sig = "";
        }

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
                req.Headers.Add("X-RediensIAM-Timestamp", timestamp);
                req.Headers.Add("X-RediensIAM-Event", job.EventType);
                req.Headers.Add("X-RediensIAM-Delivery", job.WebhookId.ToString());

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

    /// <summary>
    /// HMAC-SHA256 over <c>{timestamp}.{payload}</c>, hex-encoded. Receivers should recompute it
    /// with the shared secret and reject deliveries whose <c>X-RediensIAM-Timestamp</c> is older
    /// than their tolerance window.
    /// </summary>
    private static string ComputeSignature(string secret, string timestamp, byte[] payload)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var signed = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + payload.Length];
        var written = Encoding.UTF8.GetBytes(timestamp, signed);
        signed[written] = (byte)'.';
        payload.CopyTo(signed, written + 1);

        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        return Convert.ToHexString(hmac.ComputeHash(signed)).ToLowerInvariant();
    }
}
