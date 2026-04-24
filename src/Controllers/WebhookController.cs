using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

// ── Org-scoped webhooks ───────────────────────────────────────────────────────

[ApiController]
[Route("org/webhooks")]
[RequireManagementLevel(ManagementLevel.OrgAdmin)]
public class OrgWebhookController(
    RediensIamDbContext db,
    AppConfig appConfig,
    AuditLogService audit,
    WebhookService webhookService) : ControllerBase
{
    private const string AuditWebhook = "webhook";

    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid OrgId   => Guid.TryParse(Claims.OrgId, out var g) ? g : Guid.Empty;
    private Guid ActorId => Claims.ParsedUserId;

    [HttpGet("")]
    public async Task<IActionResult> ListWebhooks()
    {
        var webhooks = await db.Webhooks
            .Where(w => w.OrgId == OrgId && w.ProjectId == null)
            .Select(w => new { w.Id, w.Url, w.Events, w.Active, w.CreatedAt })
            .ToListAsync();
        return Ok(webhooks);
    }

    [HttpPost("")]
    public async Task<IActionResult> CreateWebhook([FromBody] CreateWebhookRequest body)
    {
        if (!body.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "url_must_be_https" });

        if (await WebhookUrlValidator.IsPrivateOrReservedAsync(body.Url))
            return BadRequest(new { error = "url_not_allowed" });

        var invalidEvents = body.Events.Except(WebhookEvents.All).ToArray();
        if (invalidEvents.Length > 0)
            return BadRequest(new { error = "invalid_events", invalid = invalidEvents });

        var rawSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var secretEnc = TotpEncryption.EncryptString(appConfig.WebhookEncKey, rawSecret);

        var wh = new Webhook
        {
            OrgId     = OrgId,
            Url       = body.Url,
            SecretEnc = secretEnc,
            Events    = body.Events,
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Webhooks.Add(wh);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "webhook.created", AuditWebhook, wh.Id.ToString());
        return Created($"/org/webhooks/{wh.Id}", new
        {
            wh.Id, wh.Url, wh.Events, wh.Active,
            secret = rawSecret,
            message = "store_secret_shown_once"
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetWebhook(Guid id)
    {
        var wh = await db.Webhooks
            .Include(w => w.Deliveries.OrderByDescending(d => d.CreatedAt).Take(10))
            .FirstOrDefaultAsync(w => w.Id == id && w.OrgId == OrgId && w.ProjectId == null);
        if (wh == null) return NotFound();
        return Ok(new
        {
            wh.Id, wh.Url, wh.Events, wh.Active, wh.CreatedAt,
            recent_deliveries = wh.Deliveries.Select(d => new
            {
                d.Id, d.Event, d.StatusCode, d.ErrorMessage, d.AttemptCount, d.DeliveredAt, d.CreatedAt
            })
        });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateWebhook(Guid id, [FromBody] UpdateWebhookRequest body)
    {
        var wh = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.OrgId == OrgId && w.ProjectId == null);
        if (wh == null) return NotFound();

        if (body.Url != null)
        {
            if (!body.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = "url_must_be_https" });
            wh.Url = body.Url;
        }
        if (body.Events != null)
        {
            var invalid = body.Events.Except(WebhookEvents.All).ToArray();
            if (invalid.Length > 0) return BadRequest(new { error = "invalid_events", invalid });
            wh.Events = body.Events;
        }
        if (body.Active.HasValue) wh.Active = body.Active.Value;

        await db.SaveChangesAsync();
        return Ok(new { wh.Id, wh.Url, wh.Events, wh.Active });
    }

    [HttpPost("{id}/rotate-secret")]
    public async Task<IActionResult> RotateSecret(Guid id)
    {
        var wh = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.OrgId == OrgId && w.ProjectId == null);
        if (wh == null) return NotFound();
        var rawSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        wh.SecretEnc = TotpEncryption.EncryptString(appConfig.WebhookEncKey, rawSecret);
        await db.SaveChangesAsync();
        return Ok(new { secret = rawSecret, message = "store_secret_shown_once" });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteWebhook(Guid id)
    {
        var wh = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.OrgId == OrgId && w.ProjectId == null);
        if (wh == null) return NotFound();
        db.Webhooks.Remove(wh);
        await db.SaveChangesAsync();
        await audit.RecordAsync(OrgId, null, ActorId, "webhook.deleted", AuditWebhook, id.ToString());
        return NoContent();
    }

    [HttpPost("{id}/test")]
    public async Task<IActionResult> TestWebhook(Guid id)
    {
        var wh = await db.Webhooks.FirstOrDefaultAsync(w => w.Id == id && w.OrgId == OrgId && w.ProjectId == null);
        if (wh == null) return NotFound();
        await webhookService.DispatchAsync("webhook.test", new { webhook_id = id, message = "test" }, OrgId, null);
        return Ok(new { message = "test_dispatched" });
    }

    [HttpGet("{id}/deliveries")]
    public async Task<IActionResult> ListDeliveries(Guid id, [FromQuery] int limit = 20, [FromQuery] int offset = 0)
    {
        limit  = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        if (!await db.Webhooks.AnyAsync(w => w.Id == id && w.OrgId == OrgId)) return NotFound();
        var deliveries = await db.WebhookDeliveries
            .Where(d => d.WebhookId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Skip(offset).Take(limit)
            .Select(d => new { d.Id, d.Event, d.StatusCode, d.ErrorMessage, d.AttemptCount, d.DeliveredAt, d.CreatedAt })
            .ToListAsync();
        return Ok(deliveries);
    }
}

// ── Admin-scoped webhooks (SuperAdmin only) ───────────────────────────────────

[ApiController]
[Route("admin/webhooks")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class AdminWebhookController(
    RediensIamDbContext db,
    AppConfig appConfig,
    AuditLogService audit) : ControllerBase
{
    private const string AuditWebhook = "webhook";

    private TokenClaims Claims => HttpContext.GetClaims()!;
    private Guid ActorId => Claims.ParsedUserId;

    [HttpGet("")]
    public async Task<IActionResult> AdminListWebhooks()
    {
        var webhooks = await db.Webhooks
            .Select(w => new { w.Id, w.OrgId, w.ProjectId, w.Url, w.Events, w.Active, w.CreatedAt })
            .ToListAsync();
        return Ok(webhooks);
    }

    [HttpPost("")]
    public async Task<IActionResult> AdminCreateWebhook([FromBody] CreateWebhookRequest body)
    {
        if (!body.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "url_must_be_https" });

        if (await WebhookUrlValidator.IsPrivateOrReservedAsync(body.Url))
            return BadRequest(new { error = "url_not_allowed" });

        var invalidEvents = body.Events.Except(WebhookEvents.All).ToArray();
        if (invalidEvents.Length > 0)
            return BadRequest(new { error = "invalid_events", invalid = invalidEvents });

        var rawSecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        var wh = new Webhook
        {
            OrgId     = null,
            Url       = body.Url,
            SecretEnc = TotpEncryption.EncryptString(appConfig.WebhookEncKey, rawSecret),
            Events    = body.Events,
            Active    = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Webhooks.Add(wh);
        await db.SaveChangesAsync();
        await audit.RecordAsync(null, null, ActorId, "webhook.created", AuditWebhook, wh.Id.ToString());
        return Created($"/admin/webhooks/{wh.Id}", new
        {
            wh.Id, wh.Url, wh.Events, wh.Active,
            secret = rawSecret,
            message = "store_secret_shown_once"
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> AdminDeleteWebhook(Guid id)
    {
        var wh = await db.Webhooks.FindAsync(id);
        if (wh == null) return NotFound();
        db.Webhooks.Remove(wh);
        await db.SaveChangesAsync();
        await audit.RecordAsync(null, null, ActorId, "webhook.deleted", AuditWebhook, id.ToString());
        return NoContent();
    }
}

public record CreateWebhookRequest(string Url, string[] Events);
public record UpdateWebhookRequest(string? Url, string[]? Events, bool? Active);

// ── Shared SSRF validator ────────────────────────────────────────────────────

public interface IWebhookSsrfValidator
{
    Task<bool> IsPrivateOrReservedAsync(string url);
}

public sealed class WebhookSsrfValidator : IWebhookSsrfValidator
{
    public Task<bool> IsPrivateOrReservedAsync(string url) =>
        WebhookUrlValidator.IsPrivateOrReservedAsync(url);
}

public static class WebhookUrlValidator
{
    public static async Task<bool> IsPrivateOrReservedAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;

        var host = uri.Host;

        if (host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".cluster.local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Any(IsPrivateIp)) return true;
        }
        catch
        {
            // DNS failure — webhook delivery will fail naturally
        }

        return false;
    }

    public static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            return ip.Equals(IPAddress.IPv6Loopback);
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 127;
    }
}
