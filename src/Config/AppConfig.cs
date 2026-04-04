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
        ?? "Host=localhost;Database=rediensiam;Username=iam;Password=changeme";

    // ── Cache / Redis ─────────────────────────────────────────────────────────
    public string CacheConnectionString => config["Cache:ConnectionString"] ?? "localhost:6379,abortConnect=false";
    public string CacheInstanceName     => config["Cache:InstanceName"] ?? "rediensiam:";
    public int    PatCacheTtlMinutes    => config.GetValue<int>("Cache:PatTtlMinutes", 5);

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
    public int    MaxLoginAttempts        => config.GetValue<int>("Security:MaxLoginAttempts", 5);
    public int    LockoutMinutes          => config.GetValue<int>("Security:LockoutMinutes", 15);
    public int    OtpTtlSeconds           => config.GetValue<int>("Security:OtpTtlSeconds", 300);
    public int    MaxSmsPerWindow         => config.GetValue<int>("Security:MaxSmsPerWindow", 3);
    public int    SmsWindowMinutes        => config.GetValue<int>("Security:SmsWindowMinutes", 10);
    public string TotpSecretEncryptionKey => config["Security:TotpSecretEncryptionKey"]
        ?? throw new InvalidOperationException("Security:TotpSecretEncryptionKey is required");
    public int    ArgonTimeCost           => config.GetValue<int>("Security:ArgonTimeCost", 3);
    public int    ArgonMemoryCost         => config.GetValue<int>("Security:ArgonMemoryCost", 65536);
    public int    ArgonParallelism        => config.GetValue<int>("Security:ArgonParallelism", 4);
    public string PatPrefix               => config["Security:PatPrefix"] ?? "rediens_pat_";

    // ── Audit ─────────────────────────────────────────────────────────────────
    public int AuditRetentionDays => config.GetValue<int>("Audit:RetentionDays", 365);

    // ── Invitations ───────────────────────────────────────────────────────────
    public int InviteExpiryHours => config.GetValue<int>("Invitations:ExpiryHours", 72);

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
    public string HydraAdminUrl  => config["Hydra:AdminUrl"]  ?? "http://rediensiam-hydra-admin:4445";
    public string HydraPublicUrl => config["Hydra:PublicUrl"] ?? "http://rediensiam-hydra-public:4444";
    public string KetoReadUrl    => config["Keto:ReadUrl"]    ?? "http://rediensiam-keto-read:4466";
    public string KetoWriteUrl   => config["Keto:WriteUrl"]   ?? "http://rediensiam-keto-write:4467";
}
