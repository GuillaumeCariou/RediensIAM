using System.Security.Cryptography;
using System.Text;

namespace RediensIAM.Config;

public class AppConfig(IConfiguration config)
{
    // ── Ports / paths ─────────────────────────────────────────────────────────
    public int    PublicPort => config.GetValue<int>("IAM_PUBLIC_PORT", 5000);
    public int    AdminPort  => config.GetValue<int>("IAM_ADMIN_PORT", 5001);
    public string AdminPath  => config["IAM_ADMIN_PATH"] ?? "/admin";

    // ── Bootstrap ─────────────────────────────────────────────────────────────
    public string? BootstrapEmail    => config["IAM_BOOTSTRAP_EMAIL"];
    public string? BootstrapPassword => config["IAM_BOOTSTRAP_PASSWORD"];

    // ── Database ──────────────────────────────────────────────────────────────
    public string ConnectionString => config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is required — set via env var ConnectionStrings__Default");

    // ── Cache / Redis ─────────────────────────────────────────────────────────
    public string CacheConnectionString => config["Cache:ConnectionString"] ?? "localhost:6379,abortConnect=false";
    public string CacheInstanceName     => config["Cache:InstanceName"] ?? "rediensiam:";
    // Upper bound on how long a revoked PAT keeps working — see MaxLoginAttempts on clamping.
    public int    PatCacheTtlMinutes    => Math.Clamp(config.GetValue<int>("Cache:PatTtlMinutes", 5), 0, 15);

    // ── App URLs ──────────────────────────────────────────────────────────────
    public string PublicUrl      => config["App:PublicUrl"] ?? "http://localhost";
    public string Domain         => config["App:Domain"] ?? throw new InvalidOperationException("App:Domain configuration is required");
    // External URL where the admin SPA is reachable (NodePort / SSH tunnel / private ingress).
    // Used for redirect_uri and post_logout_redirect in the OIDC flow.
    public string AdminSpaOrigin => config["App:AdminSpaOrigin"] ?? $"{PublicUrl}";

    // ── SMTP ──────────────────────────────────────────────────────────────────
    public string? SmtpHost        => config["Smtp:Host"];
    public int     SmtpPort        => config.GetValue<int>("Smtp:Port", 587);
    public bool    SmtpStartTls    => config.GetValue<bool>("Smtp:StartTls", true);
    public string? SmtpUsername    => config["Smtp:Username"];
    public string? SmtpPassword    => config["Smtp:Password"];
    public string  SmtpFromName    => config["Smtp:FromName"] ?? "RediensIAM";
    public string  SmtpFromAddress => config["Smtp:FromAddress"] ?? "noreply@localhost";

    // ── Security ──────────────────────────────────────────────────────────────
    // These stay operator-tunable through the instance row, so every one of them is clamped to a
    // range in which it is still a control. Unclamped, a single DB write disables account
    // lockout, weakens Argon2 for every future hash, and makes PAT revocation ineffective.
    public int    MaxLoginAttempts        => Math.Clamp(config.GetValue<int>("Security:MaxLoginAttempts", 5), 1, 10);
    public int    LockoutMinutes          => Math.Clamp(config.GetValue<int>("Security:LockoutMinutes", 15), 1, 1440);
    public int    OtpTtlSeconds           => config.GetValue<int>("Security:OtpTtlSeconds", 300);
    public int    MaxSmsPerWindow         => config.GetValue<int>("Security:MaxSmsPerWindow", 3);
    public int    SmsWindowMinutes        => config.GetValue<int>("Security:SmsWindowMinutes", 10);
    public string TotpSecretEncryptionKey => config["Security:TotpSecretEncryptionKey"]
        ?? throw new InvalidOperationException("Security:TotpSecretEncryptionKey is required");
    // Floors are the OWASP Argon2id minimum (t=2, m=19 MiB, p=1).
    public int    ArgonTimeCost           => Math.Max(config.GetValue<int>("Security:ArgonTimeCost", 3), 2);
    public int    ArgonMemoryCost         => Math.Max(config.GetValue<int>("Security:ArgonMemoryCost", 65536), 19456);
    public int    ArgonParallelism        => Math.Clamp(config.GetValue<int>("Security:ArgonParallelism", 4), 1, 16);
    /// <summary>Optional hex-encoded server-side pepper mixed via HMAC-SHA256 into Argon2 input.
    /// Empty value means no pepper (back-compat with existing hashes).</summary>
    public string Argon2Pepper            => config["Security:Argon2Pepper"] ?? "";
    public string PatPrefix               => config["Security:PatPrefix"] ?? "rediens_pat_";
    /// <summary>Upper bound on a service-account PAT's lifetime. A credential that never expires
    /// outlives every revocation path the deployment has.</summary>
    public int    MaxPatLifetimeDays      => Math.Clamp(config.GetValue<int>("Security:MaxPatLifetimeDays", 365), 1, 730);
    /// <summary>
    /// Whether the management console requires a second factor. A tenant project has
    /// <c>Project.RequireMfa</c>; RediensIAM's own admin surface — where <c>super_admin</c> lives —
    /// had no equivalent and asked for MFA only when the account happened to have a factor, so the
    /// most privileged accounts in the deployment were password-only by default. On means an admin
    /// with no factor is sent through enrolment before the login completes, never refused.
    /// </summary>
    public bool   RequireAdminMfa         => config.GetValue("Security:RequireAdminMfa", true);

    /// <summary>
    /// OAuth2 client IDs allowed to call the management surfaces (/admin, /org, /project,
    /// /service-accounts, /api/manage). CSV; defaults to the admin console client alone.
    /// A token minted for a tenant's own application client must never reach those routes —
    /// its <c>ext.roles</c> are session data, not an audience boundary.
    /// Service-account clients (<c>sa_*</c>) are additionally accepted, see
    /// <see cref="Roles.ServiceAccountClientPrefix"/>.
    /// </summary>
    public string[] ManagementClientIds =>
        (config["Security:ManagementClientIds"] ?? Roles.AdminClientId)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ── Per-purpose derived keys (HKDF-SHA256 from TotpSecretEncryptionKey) ──
    // Each purpose gets an independent 32-byte subkey; compromise of one does not
    // expose data encrypted under other purposes.
    private byte[]? _totpEncKey;
    private byte[]? _webhookEncKey;
    private byte[]? _smtpEncKey;
    private byte[]? _themeEncKey;
    private byte[]? _deviceFpKey;

    public byte[] TotpEncKey     => _totpEncKey     ??= DeriveKey("rediensiam-totp-secret-v1");
    public byte[] WebhookEncKey  => _webhookEncKey  ??= DeriveKey("rediensiam-webhook-secret-v1");
    public byte[] SmtpEncKey     => _smtpEncKey     ??= DeriveKey("rediensiam-smtp-password-v1");
    public byte[] ThemeEncKey    => _themeEncKey    ??= DeriveKey("rediensiam-theme-secret-v1");
    public byte[] DeviceFpKey    => _deviceFpKey    ??= DeriveKey("rediensiam-device-fingerprint-v1");

    private byte[] DeriveKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256,
            Convert.FromHexString(TotpSecretEncryptionKey), 32,
            info: Encoding.UTF8.GetBytes(purpose));

    // ── Audit ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shortest retention any scope may request. Retention drives an unconditional
    /// <c>ExecuteDeleteAsync</c>, so a value at or below zero is not a setting — it is a
    /// self-service purge of the evidence, including the record of the change itself.
    /// </summary>
    public const int MinAuditRetentionDays = 90;
    public const int MaxAuditRetentionDays = 3650;

    public int AuditRetentionDays => ClampRetention(config.GetValue<int>("Audit:RetentionDays", 365));

    public static int ClampRetention(int days) =>
        Math.Clamp(days, MinAuditRetentionDays, MaxAuditRetentionDays);

    // ── Invitations ───────────────────────────────────────────────────────────
    public int InviteExpiryHours => config.GetValue<int>("Invitations:ExpiryHours", 72);

    /// <summary>
    /// Link mailed to an invited user. Must point at the login SPA route that renders the
    /// set-password form, NOT at <c>/auth/invite/complete</c> — that is a POST API endpoint,
    /// so clicking the emailed link produced a 404/405.
    /// </summary>
    public string InviteUrl(string rawToken) =>
        $"{PublicUrl}/set-password?token={Uri.EscapeDataString(rawToken)}";

    // ── New device detection ──────────────────────────────────────────────────
    public int NewDeviceCacheDays => config.GetValue<int>("Security:NewDeviceCacheDays", 90);

    // ── Webhooks ──────────────────────────────────────────────────────────────
    public int WebhookTimeoutSeconds => config.GetValue<int>("Webhooks:TimeoutSeconds", 10);

    // ── Export ────────────────────────────────────────────────────────────────
    public int ExportRateLimitMinutes => config.GetValue<int>("Export:RateLimitMinutes", 1);

    // ── External services ─────────────────────────────────────────────────────
    // Override these env vars to point at external (off-cluster) service instances:
    //   Hydra__AdminUrl, Hydra__PublicUrl, Keto__ReadUrl, Keto__WriteUrl
    //   ConnectionStrings__Default, Cache__ConnectionString
    // Ory Hydra and Keto disable TLS by default for in-cluster deployments; TLS terminates at the Traefik ingress.
#pragma warning disable S5332 // In-cluster K8s service URLs — HTTP is correct; TLS terminates at the ingress
    public string HydraAdminUrl  => config["Hydra:AdminUrl"]  ?? "http://rediensiam-hydra-admin:4445";
    public string HydraPublicUrl => config["Hydra:PublicUrl"] ?? "http://rediensiam-hydra-public:4444";
    public string KetoReadUrl    => config["Keto:ReadUrl"]    ?? "http://rediensiam-keto-read:4466";
    public string KetoWriteUrl   => config["Keto:WriteUrl"]   ?? "http://rediensiam-keto-write:4467";
#pragma warning restore S5332

    // ── Social login ──────────────────────────────────────────────────────────
    public string GithubUserApiUrl   => config["Social:GithubUserApiUrl"]   ?? "https://api.github.com/user";
    public string GithubEmailsApiUrl => config["Social:GithubEmailsApiUrl"] ?? "https://api.github.com/user/emails";
}
