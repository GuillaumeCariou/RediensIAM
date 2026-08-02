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
            if (await WebhookUrlValidator.IsPrivateOrReservedAsync(body.Url))
                return BadRequest(new { error = "url_blocked_by_ssrf_policy" });
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
        await webhookService.DispatchToWebhookAsync(id, "webhook.test", new { webhook_id = id, message = "test" });
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
// Second prefix, same actions, same filter — see the note on SystemAdminController.
[Route("api/manage/webhooks")]
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
    /// <summary>
    /// Handler for every client that fetches an operator- or tenant-supplied URL.
    ///
    /// Validating the URL and then handing the hostname to the socket stack resolves DNS twice,
    /// and a record with a one-second TTL can answer "public" the first time and "169.254.169.254"
    /// the second. This resolves once, inside the connect, and dials the address it vetted — so
    /// there is no window between the check and the connection.
    ///
    /// The callback vets redirect hops too, so <paramref name="allowAutoRedirect"/> is no longer
    /// a security control — it stays false where following a redirect was never wanted anyway
    /// (webhook delivery, OAuth2 token exchange).
    /// </summary>
    public static SocketsHttpHandler CreateSsrfSafeHandler(bool allowAutoRedirect = false) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var vetted = Array.Find(addresses, a => !IsPrivateIp(a))
                ?? throw new HttpRequestException(
                    $"Refused to connect to {ctx.DnsEndPoint.Host}: resolves only to private or reserved addresses");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(vetted, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static async Task<bool> IsPrivateOrReservedAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;
        return await IsPrivateOrReservedHostAsync(uri.Host);
    }

    /// <summary>
    /// Host-only entry point, for targets that are not URLs — an SMTP endpoint is a bare
    /// host:port pair and used to reach the network without passing through here at all.
    /// </summary>
    public static async Task<bool> IsPrivateOrReservedHostAsync(string host)
    {
        // Uri.Host keeps the brackets on an IPv6 literal, and Dns.GetHostAddressesAsync throws
        // on the bracketed form — which the catch below turned into "allowed".
        host = host.Trim('[', ']');

        if (host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".cluster.local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out var literal)) return IsPrivateIp(literal);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Any(IsPrivateIp)) return true;
        }
        catch
        {
            // A resolver failure is not evidence either way, and denying on it refuses legitimate
            // hosts that only resolve from inside the cluster — it broke SAML metadata fetches and
            // every SMTP config whose host does not resolve from the API pod. The rebinding case
            // this looked like it addressed is covered where it actually matters: webhooks, OIDC
            // and SAML dial through the SSRF-safe connect callback, which re-checks the address it
            // is about to connect to, and NotificationService re-validates the SMTP host at send
            // time rather than trusting what was stored.
        }

        return false;
    }

    public static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        // Normalise IPv4-mapped IPv6 (::ffff:10.0.0.1) BEFORE the v6 branch. Without this the
        // v6 branch returns early and every private IPv4 range is reachable by writing it in
        // mapped form — the address resolves to exactly the same host.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return IsPrivateIpv6(ip);

        return IsPrivateIpv4(ip.GetAddressBytes());
    }

    private static bool IsPrivateIpv6(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6Any)) return true;
        var v6 = ip.GetAddressBytes();
        // fc00::/7 — unique-local, the IPv6 equivalent of RFC1918. IsIPv6SiteLocal only
        // covers the deprecated fec0::/10 and misses this entirely.
        if ((v6[0] & 0xFE) == 0xFC) return true;
        // 2001:db8::/32 — documentation range, never legitimate egress.
        return v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8;
    }

    private static bool IsPrivateIpv4(byte[] b)
    {
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)          // link-local + cloud metadata
            || b[0] == 127
            || b[0] == 0                              // "this network"
            // 100.64.0.0/10 — CGNAT, and the range Tailscale hands out. This deployment
            // exposes its admin ingress on 100.64.0.3.
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 192 && b[1] == 0 && b[2] == 0)       // 192.0.0.0/24 IETF protocol assignments
            || (b[0] == 198 && (b[1] & 0xFE) == 18)          // 198.18.0.0/15 benchmarking
            || b[0] >= 224;                                   // multicast + reserved
    }
}
