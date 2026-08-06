using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Filters;
using RediensIAM.Middleware;
using RediensIAM.Services;

namespace RediensIAM.Controllers;

/// <summary>
/// The deployment's runtime configuration — the settings the `instances` row already held and
/// nothing could write.
///
/// <para>
/// The row has carried some twenty settings since 0.5.0, seeded from the environment at boot and
/// never touched again: changing a lockout threshold meant editing a manifest and rolling the pods.
/// This is the write half that was missing, and the read half that lets a console show what is
/// actually in force rather than what somebody believes they deployed.
/// </para>
///
/// <para>
/// <b>What is deliberately not here.</b> Four groups stay environment-only, and the allow-list
/// below is what enforces it rather than a comment asking people to be careful:
/// </para>
///
/// <list type="bullet">
/// <item><c>Argon*</c> — they drive the pod's memory limit. Raising the cost from a browser kills
/// the pod that served the request, and the operator's next act is to wonder why the console is
/// down.</item>
/// <item>Trust anchors — <c>Hydra:*</c>, <c>Keto:*</c>, <c>App:TrustedProxies</c>. A process must
/// not learn who to trust from data it can write.</item>
/// <item>Topology — <c>App:PublicUrl</c>, <c>App:AdminSpaOrigin</c>, <c>App:Domain</c>. Decided by
/// whoever deploys; changing them from inside invalidates the OAuth2 client registration that
/// the console is currently authenticated through.</item>
/// <item>Secrets. They are never in this row to begin with.</item>
/// </list>
/// </summary>
[ApiController]
[Route("admin/instance")]
[Route("api/manage/instance")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class InstanceController(
    RediensIamDbContext db,
    IConfiguration configuration,
    AuditLogService audit,
    AppConfig appConfig) : ControllerBase
{
    private Guid ActorId => HttpContext.GetClaims()!.ParsedUserId;

    private const string InstanceEntity = "instance";

    [HttpGet("")]
    public async Task<IActionResult> Get()
    {
        var row = await Row();
        if (row is null) return NotFound(new { error = "instance_row_missing" });

        return Ok(new
        {
            row.Id,
            config_version = row.ConfigVersion,
            updated_at     = row.UpdatedAt,
            reconfigured_at = row.ReconfiguredAt,

            // What is actually in force on this pod. Not necessarily what is stored: an
            // environment variable added after the instance provider wins over the row, and an
            // operator staring at a setting that "will not change" needs to see that rather than
            // deduce it. `stored` below is what a PATCH wrote; a difference between the two is the
            // answer to the question they are about to ask.
            settings = new
            {
                max_login_attempts   = appConfig.MaxLoginAttempts,
                lockout_minutes      = appConfig.LockoutMinutes,
                otp_ttl_seconds      = appConfig.OtpTtlSeconds,
                max_sms_per_window   = appConfig.MaxSmsPerWindow,
                sms_window_minutes   = appConfig.SmsWindowMinutes,
                audit_retention_days = appConfig.AuditRetentionDays,
                invite_expiry_hours  = appConfig.InviteExpiryHours,
                pat_cache_ttl_minutes = appConfig.PatCacheTtlMinutes,
                smtp_host            = appConfig.SmtpHost ?? "",
                smtp_port            = appConfig.SmtpPort,
                smtp_start_tls       = appConfig.SmtpStartTls,
                smtp_username        = appConfig.SmtpUsername ?? "",
                smtp_from_address    = appConfig.SmtpFromAddress,
                smtp_from_name       = appConfig.SmtpFromName,
            },

            // What the row holds — what a PATCH last wrote, before the environment had its say.
            stored = new
            {
                max_login_attempts    = row.MaxLoginAttempts,
                lockout_minutes       = row.LockoutMinutes,
                otp_ttl_seconds       = row.OtpTtlSeconds,
                max_sms_per_window    = row.MaxSmsPerWindow,
                sms_window_minutes    = row.SmsWindowMinutes,
                audit_retention_days  = row.AuditRetentionDays,
                invite_expiry_hours   = row.InviteExpiryHours,
                pat_cache_ttl_minutes = row.PatCacheTtlMinutes,
                smtp_host             = row.SmtpHost,
                smtp_port             = row.SmtpPort,
                smtp_start_tls        = row.SmtpStartTls,
                smtp_username         = row.SmtpUsername,
                smtp_from_address     = row.SmtpFromAddress,
                smtp_from_name        = row.SmtpFromName,
            },

            // Read-only, and shown rather than hidden: an operator looking for "why is this value
            // not what I set" needs to see the ones this endpoint will never change.
            environment_only = new
            {
                public_url        = appConfig.PublicUrl,
                admin_spa_origin  = appConfig.AdminSpaOrigin,
                domain            = row.Domain,
                trusted_proxies   = appConfig.TrustedProxies ?? "",
                hydra_admin_url   = appConfig.HydraAdminUrl,
                hydra_public_url  = appConfig.HydraPublicUrl,
                keto_read_url     = appConfig.KetoReadUrl,
                keto_write_url    = appConfig.KetoWriteUrl,
                argon_time_cost   = appConfig.ArgonTimeCost,
                argon_memory_cost = appConfig.ArgonMemoryCost,
                argon_parallelism = appConfig.ArgonParallelism,
            },
        });
    }

    /// <summary>
    /// Writes the settings a caller named, leaves the rest alone, and makes the change take effect
    /// without a restart.
    ///
    /// <para>
    /// Every value goes through the same <c>AppConfig.Clamp*</c> the environment path uses, so a
    /// setting cannot mean one thing typed into a form and another declared in a manifest. Out of
    /// range is <b>clamped, not refused</b>: the request states an intent — "make lockout longer" —
    /// and answering 400 to 100000 minutes teaches the operator nothing that returning 1440 does
    /// not. The answer carries what was actually stored.
    /// </para>
    /// </summary>
    [HttpPatch("")]
    public async Task<IActionResult> Patch([FromBody] UpdateInstanceRequest body)
    {
        var row = await Row();
        if (row is null) return NotFound(new { error = "instance_row_missing" });

        var changed = new Dictionary<string, object>();
        void Set<T>(string name, T? requested, T current, Action<T> apply) where T : struct
        {
            if (requested is not { } v || v.Equals(current)) return;
            apply(v);
            changed[name] = v;
        }
        void SetText(string name, string? requested, string current, Action<string> apply)
        {
            if (requested is null || requested == current) return;
            apply(requested);
            changed[name] = requested;
        }

        Set("max_login_attempts",    body.MaxLoginAttempts is { } a ? AppConfig.ClampMaxLoginAttempts(a) : null,   row.MaxLoginAttempts,   v => row.MaxLoginAttempts = v);
        Set("lockout_minutes",       body.LockoutMinutes is { } b ? AppConfig.ClampLockoutMinutes(b) : null,       row.LockoutMinutes,     v => row.LockoutMinutes = v);
        Set("otp_ttl_seconds",       body.OtpTtlSeconds is { } c ? AppConfig.ClampOtpTtlSeconds(c) : null,         row.OtpTtlSeconds,      v => row.OtpTtlSeconds = v);
        Set("max_sms_per_window",    body.MaxSmsPerWindow is { } d ? AppConfig.ClampMaxSmsPerWindow(d) : null,     row.MaxSmsPerWindow,    v => row.MaxSmsPerWindow = v);
        Set("sms_window_minutes",    body.SmsWindowMinutes is { } e ? AppConfig.ClampSmsWindowMinutes(e) : null,   row.SmsWindowMinutes,   v => row.SmsWindowMinutes = v);
        Set("audit_retention_days",  body.AuditRetentionDays is { } f ? AppConfig.ClampRetention(f) : null,        row.AuditRetentionDays, v => row.AuditRetentionDays = v);
        Set("invite_expiry_hours",   body.InviteExpiryHours is { } g ? AppConfig.ClampInviteExpiryHours(g) : null, row.InviteExpiryHours,  v => row.InviteExpiryHours = v);
        Set("pat_cache_ttl_minutes", body.PatCacheTtlMinutes is { } h ? AppConfig.ClampPatCacheTtl(h) : null,      row.PatCacheTtlMinutes, v => row.PatCacheTtlMinutes = v);
        Set("smtp_port",             body.SmtpPort is { } i ? AppConfig.ClampSmtpPort(i) : null,                   row.SmtpPort,           v => row.SmtpPort = v);
        Set("smtp_start_tls",        body.SmtpStartTls,                                                            row.SmtpStartTls,       v => row.SmtpStartTls = v);

        SetText("smtp_host",         body.SmtpHost,        row.SmtpHost,        v => row.SmtpHost = v);
        SetText("smtp_username",     body.SmtpUsername,    row.SmtpUsername,    v => row.SmtpUsername = v);
        SetText("smtp_from_address", body.SmtpFromAddress, row.SmtpFromAddress, v => row.SmtpFromAddress = v);
        SetText("smtp_from_name",    body.SmtpFromName,    row.SmtpFromName,    v => row.SmtpFromName = v);

        if (changed.Count == 0) return Ok(new { changed = Array.Empty<string>(), config_version = row.ConfigVersion });

        row.ConfigVersion  += 1;
        row.UpdatedAt       = DateTimeOffset.UtcNow;
        row.ReconfiguredAt  = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Deployment-wide, so the audit row carries no organisation: it belongs to the deployment's
        // own chain, which is where a super-admin's acts are read from.
        await audit.RecordAsync(null, null, ActorId, "instance.updated", InstanceEntity, row.Id,
            changed.ToDictionary(k => k.Key, k => (object)k.Value.ToString()!));

        // What makes this take effect. The instance row is an IConfiguration provider, and
        // AppConfig reads through IConfiguration on every property access rather than caching at
        // construction — so a reload is the whole propagation story, on this pod. Other replicas
        // pick the change up at their next reload; `ConfigVersion` is what makes that observable.
        (configuration as IConfigurationRoot)?.Reload();

        return Ok(new { changed = changed.Keys, config_version = row.ConfigVersion });
    }

    private Task<Data.Entities.Instance?> Row() =>
        db.Instances.FirstOrDefaultAsync(i => i.Id == appConfig.InstanceId);
}

/// <summary>
/// Every field optional: a PATCH states what changes, and a body naming one setting must not reset
/// the nineteen it does not name.
///
/// <para>
/// The environment-only settings are absent from this record on purpose. Declaring them so they
/// could be refused would make them look settable; leaving them out means a caller that sends one
/// has it discarded by the binder, which is the same outcome with less surface.
/// </para>
/// </summary>
public record UpdateInstanceRequest(
    int? MaxLoginAttempts = null,
    int? LockoutMinutes = null,
    int? OtpTtlSeconds = null,
    int? MaxSmsPerWindow = null,
    int? SmsWindowMinutes = null,
    int? AuditRetentionDays = null,
    int? InviteExpiryHours = null,
    int? PatCacheTtlMinutes = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    bool? SmtpStartTls = null,
    string? SmtpUsername = null,
    string? SmtpFromAddress = null,
    string? SmtpFromName = null);
