using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Filters;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus { Ok, Error, NotConfigured }

public sealed record ComponentHealth(
    string Name,
    string Category,
    HealthStatus Status,
    long? LatencyMs,
    string? Detail,
    IReadOnlyDictionary<string, string>? Stats = null);

[ApiController]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class SystemHealthController(
    RediensIamDbContext db,
    IDistributedCache cache,
    AppConfig appConfig,
    IHttpClientFactory httpClientFactory,
    IEmailService emailService) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [HttpGet("/admin/system/health")]
    public async Task<IActionResult> GetHealth()
    {
        var checks = await Task.WhenAll(
            CheckDatabase(),
            CheckCache(),
            CheckHydraAdmin(),
            CheckHydraPublic(),
            CheckKetoRead(),
            CheckKetoWrite(),
            CheckSmtp()
        );

        var overall = checks.Any(c => c.Status == HealthStatus.Error) ? "error" : "ok";
        return Ok(new { overall, checks });
    }

    // ── Checks ────────────────────────────────────────────────────────────────

    private async Task<ComponentHealth> CheckDatabase()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            sw.Stop();

            // Gather app-level stats while we have a working connection
            var stats = new Dictionary<string, string>();
            try
            {
                var dbSize = await db.Database
                    .SqlQueryRaw<string>("SELECT pg_size_pretty(pg_database_size(current_database())) AS \"Value\"")
                    .FirstOrDefaultAsync();
                if (dbSize != null) stats["db_size"] = dbSize;

                stats["users"]         = (await db.Users.CountAsync()).ToString();
                stats["organisations"] = (await db.Organisations.Where(o => o.Slug != "__system__").CountAsync()).ToString();
                stats["projects"]      = (await db.Projects.CountAsync()).ToString();
                stats["webhooks"]      = (await db.Webhooks.CountAsync(w => w.Active)).ToString() + " active";
            }
            catch { /* stats are best-effort */ }

            return new ComponentHealth("PostgreSQL", "Storage", HealthStatus.Ok, sw.ElapsedMilliseconds, null, stats);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Err("PostgreSQL", "Storage", ex, sw);
        }
    }

    private async Task<ComponentHealth> CheckCache()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            const string key = "__health__";
            await cache.SetStringAsync(key, "1", new DistributedCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5) });
            var val = await cache.GetStringAsync(key);
            if (val != "1") throw new Exception("cache round-trip mismatch");
            return Ok("Dragonfly", "Storage", sw);
        }
        catch (Exception ex) { return Err("Dragonfly", "Storage", ex, sw); }
    }

    private async Task<ComponentHealth> CheckHydraAdmin()
    {
        var (status, latency, detail) = await Probe($"{appConfig.HydraAdminUrl}/health/alive");
        if (status != HealthStatus.Ok)
            return new ComponentHealth("Hydra (admin)", "Ory", status, latency, detail);

        var stats = new Dictionary<string, string>();
        try
        {
            var version = await FetchVersion($"{appConfig.HydraAdminUrl}/version");
            if (version != null) stats["version"] = version;
            // OAuth2 clients managed by this app
            var clients = await db.Projects.CountAsync(p => p.HydraClientId != null);
            stats["oauth2_clients"] = clients.ToString();
        }
        catch { /* best-effort */ }

        return new ComponentHealth("Hydra (admin)", "Ory", HealthStatus.Ok, latency, null, stats);
    }

    private async Task<ComponentHealth> CheckHydraPublic()
    {
        var (status, latency, detail) = await Probe($"{appConfig.HydraPublicUrl}/health/alive");
        if (status != HealthStatus.Ok)
            return new ComponentHealth("Hydra (public)", "Ory", status, latency, detail);

        var stats = new Dictionary<string, string>();
        try
        {
            var version = await FetchVersion($"{appConfig.HydraPublicUrl}/version");
            if (version != null) stats["version"] = version;
        }
        catch { /* best-effort */ }

        return new ComponentHealth("Hydra (public)", "Ory", HealthStatus.Ok, latency, null, stats);
    }

    private async Task<ComponentHealth> CheckKetoRead()
    {
        var (status, latency, detail) = await Probe($"{appConfig.KetoReadUrl}/health/alive");
        if (status != HealthStatus.Ok)
            return new ComponentHealth("Keto (read)", "Ory", status, latency, detail);

        var stats = new Dictionary<string, string>();
        try
        {
            var version = await FetchVersion($"{appConfig.KetoReadUrl}/version");
            if (version != null) stats["version"] = version;
        }
        catch { /* best-effort */ }

        return new ComponentHealth("Keto (read)", "Ory", HealthStatus.Ok, latency, null, stats);
    }

    private async Task<ComponentHealth> CheckKetoWrite()
    {
        var (status, latency, detail) = await Probe($"{appConfig.KetoWriteUrl}/health/alive");
        if (status != HealthStatus.Ok)
            return new ComponentHealth("Keto (write)", "Ory", status, latency, detail);

        var stats = new Dictionary<string, string>();
        try
        {
            var version = await FetchVersion($"{appConfig.KetoWriteUrl}/version");
            if (version != null) stats["version"] = version;
        }
        catch { /* best-effort */ }

        return new ComponentHealth("Keto (write)", "Ory", HealthStatus.Ok, latency, null, stats);
    }

    private async Task<ComponentHealth> CheckSmtp()
    {
        if (string.IsNullOrEmpty(appConfig.SmtpHost))
            return new ComponentHealth("SMTP", "Email", HealthStatus.NotConfigured, null,
                "No global SMTP host configured — per-org SMTP still works if set individually.");

        var sw = Stopwatch.StartNew();
        try
        {
            await emailService.CheckConnectivityAsync();
            sw.Stop();
            var stats = new Dictionary<string, string>
            {
                ["host"]     = appConfig.SmtpHost,
                ["port"]     = appConfig.SmtpPort.ToString(),
                ["starttls"] = appConfig.SmtpStartTls ? "yes" : "no",
                ["auth"]     = string.IsNullOrEmpty(appConfig.SmtpUsername) ? "none" : appConfig.SmtpUsername,
            };
            return new ComponentHealth("SMTP", "Email", HealthStatus.Ok, sw.ElapsedMilliseconds, null, stats);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Err("SMTP", "Email", ex, sw);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(HealthStatus status, long latency, string? detail)> Probe(string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = httpClientFactory.CreateClient("health");
            using var resp = await client.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            sw.Stop();
            return (HealthStatus.Ok, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (HealthStatus.Error, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private async Task<string?> FetchVersion(string url)
    {
        using var client = httpClientFactory.CreateClient("health");
        using var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync();
        var doc  = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
        return doc.TryGetProperty("version", out var v) ? v.GetString() : null;
    }

    private static ComponentHealth Ok(string name, string category, Stopwatch sw, string? detail = null)
    {
        sw.Stop();
        return new ComponentHealth(name, category, HealthStatus.Ok, sw.ElapsedMilliseconds, detail);
    }

    private static ComponentHealth Err(string name, string category, Exception ex, Stopwatch sw)
    {
        sw.Stop();
        return new ComponentHealth(name, category, HealthStatus.Error, sw.ElapsedMilliseconds, ex.Message);
    }
}
