using Microsoft.EntityFrameworkCore;
using RediensIAM.Data;
using RediensIAM.Data.Entities;

namespace RediensIAM.Config;

/// <summary>
/// IConfiguration provider that loads runtime (non-secret) config from the
/// <c>instances</c> table. Layered on top of env/appsettings so values from the
/// DB take precedence — env vars become dormant after first boot.
///
/// Bootstrap behaviour:
///   - Row missing → write env values into the row, then load them.
///   - Row present + <c>RECONFIGURE_FROM_ENV=true</c> → overwrite from env,
///     bump <see cref="Instance.ConfigVersion"/>, then load.
///   - Row present + no reconfigure flag → load existing values, ignore env.
///
/// Secrets (DB connection, encryption keys, bootstrap admin password) are
/// intentionally NOT in the instance table; they remain env-only.
/// </summary>
public sealed class InstanceConfigurationProvider(InstanceBootstrapOptions opts) : ConfigurationProvider
{
    public override void Load()
    {
        // Provider runs before DI is built — open a one-shot DbContext from the
        // connection string directly. Migrations are idempotent so running them
        // here is safe even though Program.cs also retries them on startup.
        var dbOpts = new DbContextOptionsBuilder<RediensIamDbContext>()
            .UseNpgsql(opts.ConnectionString).Options;

        try
        {
            using var db = new RediensIamDbContext(dbOpts);
            db.Database.Migrate();

            var now = DateTimeOffset.UtcNow;
            var inst = db.Instances.FirstOrDefault(i => i.Id == opts.InstanceId);
            if (inst == null)
            {
                inst = NewFromEnv(opts.InstanceId, opts.EnvDefaults, now);
                db.Instances.Add(inst);
                db.SaveChanges();
            }
            else if (opts.ReconfigureFromEnv)
            {
                ApplyEnv(inst, opts.EnvDefaults);
                inst.ConfigVersion++;
                inst.UpdatedAt = now;
                inst.ReconfiguredAt = now;
                db.SaveChanges();
            }
            Data = ToDict(inst);
        }
        catch (Exception ex)
        {
            // DB unreachable: degrade to env-only so the app at least logs a clear error.
            Console.Error.WriteLine($"WARNING: instance config unavailable ({ex.GetType().Name}: {ex.Message}); using env values for this boot.");
            Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ── env → entity ─────────────────────────────────────────────────────────

    private static Instance NewFromEnv(string id, IReadOnlyDictionary<string, string?> env, DateTimeOffset now)
    {
        var i = new Instance { Id = id, CreatedAt = now, UpdatedAt = now };
        ApplyEnv(i, env);
        return i;
    }

    private static void ApplyEnv(Instance i, IReadOnlyDictionary<string, string?> env)
    {
        string S(string k, string fallback)      => string.IsNullOrWhiteSpace(env.GetValueOrDefault(k)) ? fallback : env[k]!;
        int    I(string k, int fallback)         => int.TryParse(env.GetValueOrDefault(k), out var v) ? v : fallback;
        bool   B(string k, bool fallback)        => bool.TryParse(env.GetValueOrDefault(k), out var v) ? v : fallback;

        i.PublicUrl       = S("App:PublicUrl",       i.PublicUrl);
        i.AdminSpaOrigin  = S("App:AdminSpaOrigin",  i.AdminSpaOrigin);
        i.Domain          = S("App:Domain",          i.Domain);
        i.AdminPath       = S("IAM_ADMIN_PATH",      i.AdminPath);
        i.PublicPort      = I("IAM_PUBLIC_PORT",     i.PublicPort);
        i.AdminPort       = I("IAM_ADMIN_PORT",      i.AdminPort);

        // Trust anchors (App:TrustedProxies, Hydra:*Url, Keto:*Url) are deliberately absent:
        // see the note above ToDict.

        i.SmtpHost        = S("Smtp:Host",           i.SmtpHost);
        i.SmtpPort        = I("Smtp:Port",           i.SmtpPort);
        i.SmtpStartTls    = B("Smtp:StartTls",       i.SmtpStartTls);
        i.SmtpUsername    = S("Smtp:Username",       i.SmtpUsername);
        i.SmtpFromAddress = S("Smtp:FromAddress",    i.SmtpFromAddress);
        i.SmtpFromName    = S("Smtp:FromName",       i.SmtpFromName);

        i.CacheInstanceName  = S("Cache:InstanceName",  i.CacheInstanceName);
        i.PatCacheTtlMinutes = I("Cache:PatTtlMinutes", i.PatCacheTtlMinutes);

        i.MaxLoginAttempts  = I("Security:MaxLoginAttempts",  i.MaxLoginAttempts);
        i.LockoutMinutes    = I("Security:LockoutMinutes",    i.LockoutMinutes);
        i.OtpTtlSeconds     = I("Security:OtpTtlSeconds",     i.OtpTtlSeconds);
        i.MaxSmsPerWindow   = I("Security:MaxSmsPerWindow",   i.MaxSmsPerWindow);
        i.SmsWindowMinutes  = I("Security:SmsWindowMinutes",  i.SmsWindowMinutes);
        i.ArgonTimeCost     = I("Security:ArgonTimeCost",     i.ArgonTimeCost);
        i.ArgonMemoryCost   = I("Security:ArgonMemoryCost",   i.ArgonMemoryCost);
        i.ArgonParallelism  = I("Security:ArgonParallelism",  i.ArgonParallelism);
        i.AuditRetentionDays = I("Audit:RetentionDays",       i.AuditRetentionDays);
        i.InviteExpiryHours  = I("Invitations:ExpiryHours",   i.InviteExpiryHours);
    }

    // ── entity → flat IConfiguration dict ────────────────────────────────────

    /// <summary>
    /// Emits the operational configuration only.
    ///
    /// <c>Hydra:AdminUrl</c>, <c>Keto:ReadUrl</c>/<c>WriteUrl</c> and <c>App:TrustedProxies</c>
    /// are deliberately NOT emitted, even though the columns still exist. They decide *who the
    /// process believes* — where tokens are introspected, where authorisation resolves, and whose
    /// <c>X-Forwarded-For</c> is trusted — and this row is written with the same Postgres
    /// credentials Hydra and Keto hold. A process must not learn who to trust from data it can
    /// itself write; fail-closed on an unreachable Keto defends against Keto being *down*, not
    /// against Keto being *someone else*. These come from env/appsettings only, which is already
    /// how the chart supplies them.
    /// </summary>
    private static Dictionary<string, string?> ToDict(Instance i) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["App:PublicUrl"]               = i.PublicUrl,
        ["App:AdminSpaOrigin"]          = i.AdminSpaOrigin,
        ["App:Domain"]                  = i.Domain,
        ["IAM_ADMIN_PATH"]              = i.AdminPath,
        ["IAM_PUBLIC_PORT"]             = i.PublicPort.ToString(),
        ["IAM_ADMIN_PORT"]              = i.AdminPort.ToString(),

        ["Smtp:Host"]                   = i.SmtpHost,
        ["Smtp:Port"]                   = i.SmtpPort.ToString(),
        ["Smtp:StartTls"]               = i.SmtpStartTls.ToString(),
        ["Smtp:Username"]               = i.SmtpUsername,
        ["Smtp:FromAddress"]            = i.SmtpFromAddress,
        ["Smtp:FromName"]               = i.SmtpFromName,

        ["Cache:InstanceName"]          = i.CacheInstanceName,
        ["Cache:PatTtlMinutes"]         = i.PatCacheTtlMinutes.ToString(),

        ["Security:MaxLoginAttempts"]   = i.MaxLoginAttempts.ToString(),
        ["Security:LockoutMinutes"]     = i.LockoutMinutes.ToString(),
        ["Security:OtpTtlSeconds"]      = i.OtpTtlSeconds.ToString(),
        ["Security:MaxSmsPerWindow"]    = i.MaxSmsPerWindow.ToString(),
        ["Security:SmsWindowMinutes"]   = i.SmsWindowMinutes.ToString(),
        ["Security:ArgonTimeCost"]      = i.ArgonTimeCost.ToString(),
        ["Security:ArgonMemoryCost"]    = i.ArgonMemoryCost.ToString(),
        ["Security:ArgonParallelism"]   = i.ArgonParallelism.ToString(),
        ["Audit:RetentionDays"]         = i.AuditRetentionDays.ToString(),
        ["Invitations:ExpiryHours"]     = i.InviteExpiryHours.ToString(),
    };
}

public sealed record InstanceBootstrapOptions(
    string ConnectionString,
    string InstanceId,
    bool ReconfigureFromEnv,
    IReadOnlyDictionary<string, string?> EnvDefaults);

public sealed class InstanceConfigurationSource(InstanceBootstrapOptions opts) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new InstanceConfigurationProvider(opts);
}

public static class InstanceConfigurationExtensions
{
    /// <summary>
    /// Layers DB-stored instance config on top of env/appsettings. Skips silently
    /// if no DB connection string is configured (test isolation, design-time tooling).
    /// </summary>
    public static IConfigurationBuilder AddInstanceConfiguration(this IConfigurationBuilder builder)
    {
        var snapshot = builder.Build();
        var conn = snapshot["ConnectionStrings:Default"];
        if (string.IsNullOrWhiteSpace(conn)) return builder;

        var instanceId = snapshot["INSTANCE_ID"] ?? "default";
        var reconfigure = bool.TryParse(snapshot["RECONFIGURE_FROM_ENV"], out var r) && r;
        var env = snapshot.AsEnumerable()
            .Where(kv => kv.Value != null)
            .ToDictionary<KeyValuePair<string, string?>, string, string?>(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        builder.Add(new InstanceConfigurationSource(new InstanceBootstrapOptions(conn, instanceId, reconfigure, env)));
        return builder;
    }
}
