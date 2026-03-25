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
    public string AdminSpaOrigin => config["App:AdminSpaOrigin"] ?? "http://localhost:5001";

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

    // ── External services ─────────────────────────────────────────────────────
    public string KetoReadUrl   => config["Keto:ReadUrl"]   ?? "http://keto-read:4466";
    public string KetoWriteUrl  => config["Keto:WriteUrl"]  ?? "http://keto-write:4467";
    public string HydraAdminUrl => config["Hydra:AdminUrl"] ?? "http://hydra:4445";
}
