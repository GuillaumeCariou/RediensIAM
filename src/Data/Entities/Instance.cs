namespace RediensIAM.Data.Entities;

/// <summary>
/// Stateless-config row. The runtime (non-secret) configuration of a RediensIAM
/// instance is stored in this single-row table so multiple pods can share it and
/// horizontal scaling does not require synchronised env vars.
///
/// Secrets (DB connection, encryption keys, KMS roots, bootstrap admin password)
/// remain env-only — they are needed to read this row in the first place.
///
/// Bootstrap rules (handled by <see cref="RediensIAM.Config.InstanceConfigurationProvider"/>):
///   1. First start: row missing → read env vars → write row → use those values.
///   2. Normal start: row present → load values into IConfiguration → ignore env
///      for these keys (env vars stay defined in the deployment manifest as the
///      record of last reconfigure, but the app reads from the DB).
///   3. RECONFIGURE_FROM_ENV=true: re-read env → overwrite row → bump
///      <see cref="ConfigVersion"/>.
/// </summary>
public class Instance
{
    /// <summary>Primary key. Defaults to "default" for single-instance deployments;
    /// set via INSTANCE_ID env to support multi-tenant clusters.</summary>
    public string Id { get; set; } = "default";

    // ── App ───────────────────────────────────────────────────────────────────
    public string PublicUrl       { get; set; } = "";
    public string AdminSpaOrigin  { get; set; } = "";
    public string Domain          { get; set; } = "";
    /// <summary>
    /// Unused. It never decided where the console is served — that is <c>Roles.ConsoleBasePath</c>
    /// — and nothing reads this column. Kept because dropping it is a migration on a live database
    /// for no gain; the plumbing that fed it is gone.
    /// </summary>
    public string AdminPath       { get; set; } = "/console";
    public string TrustedProxies  { get; set; } = "";

    // ── Ports ─────────────────────────────────────────────────────────────────
    public int PublicPort { get; set; } = 5000;
    public int AdminPort  { get; set; } = 5001;

    // ── Hydra / Keto ──────────────────────────────────────────────────────────
    public string HydraAdminUrl  { get; set; } = "";
    public string HydraPublicUrl { get; set; } = "";
    public string KetoReadUrl    { get; set; } = "";
    public string KetoWriteUrl   { get; set; } = "";

    // ── SMTP (global; per-org SMTP overrides this) ────────────────────────────
    public string SmtpHost        { get; set; } = "";
    public int    SmtpPort        { get; set; } = 587;
    public bool   SmtpStartTls    { get; set; } = true;
    public string SmtpUsername    { get; set; } = "";
    public string SmtpFromAddress { get; set; } = "noreply@localhost";
    public string SmtpFromName    { get; set; } = "RediensIAM";

    // ── Cache ─────────────────────────────────────────────────────────────────
    public string CacheInstanceName  { get; set; } = "rediensiam:";
    public int    PatCacheTtlMinutes { get; set; } = 5;

    // ── Security knobs (NOT secrets) ─────────────────────────────────────────
    public int MaxLoginAttempts  { get; set; } = 5;
    public int LockoutMinutes    { get; set; } = 15;
    public int OtpTtlSeconds     { get; set; } = 300;
    public int MaxSmsPerWindow   { get; set; } = 3;
    public int SmsWindowMinutes  { get; set; } = 10;
    public int ArgonTimeCost     { get; set; } = 3;
    public int ArgonMemoryCost   { get; set; } = 65536;
    public int ArgonParallelism  { get; set; } = 4;
    public int AuditRetentionDays { get; set; } = 365;
    public int InviteExpiryHours { get; set; } = 72;

    /// <summary>
    /// The environment values last applied to this row, as JSON, keyed by configuration key.
    ///
    /// <para>
    /// It exists to tell two changes apart. The deployment must be able to describe itself — an
    /// operator editing the chart has to see it take effect, and a row frozen at first boot was a
    /// real defect. But since 0.7.0 an operator can also change these settings from the console,
    /// and re-applying the environment on every load would silently undo that at the next restart:
    /// a control that does not hold is worse than an absent one.
    /// </para>
    ///
    /// <para>
    /// So the environment is applied when <b>it</b> changed, not merely because it is present. A
    /// key whose environment value is what it was last time leaves the stored value alone, which is
    /// what makes a console write survive a rollout.
    /// </para>
    /// </summary>
    public string EnvSnapshot { get; set; } = "{}";

    // ── Versioning + audit ────────────────────────────────────────────────────
    /// <summary>Monotonically increasing; bumped on every reconfigure. Future use:
    /// pods poll this to refresh in-memory config without restart.</summary>
    public long ConfigVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt      { get; set; }
    public DateTimeOffset UpdatedAt      { get; set; }
    public DateTimeOffset? ReconfiguredAt { get; set; }
}
