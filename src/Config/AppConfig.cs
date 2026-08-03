using System.Security.Cryptography;
using System.Text;

namespace RediensIAM.Config;

public class AppConfig(IConfiguration config)
{
    // ── Ports / paths ─────────────────────────────────────────────────────────
    public int    PublicPort => config.GetValue<int>("IAM_PUBLIC_PORT", 5000);
    public int    AdminPort  => config.GetValue<int>("IAM_ADMIN_PORT", 5001);

    // ── Bootstrap ─────────────────────────────────────────────────────────────
    public string? BootstrapEmail    => config["IAM_BOOTSTRAP_EMAIL"];
    public string? BootstrapPassword => config["IAM_BOOTSTRAP_PASSWORD"];

    // ── Database ──────────────────────────────────────────────────────────────
    public string ConnectionString => RequirePerCheckoutSessionState(
        config.GetConnectionString("Default")
        ?? throw new InvalidOperationException("ConnectionStrings:Default is required — set via env var ConnectionStrings__Default"));

    /// <summary>
    /// Whether startup applies EF migrations. Default true — that is the behaviour every existing
    /// deployment already has, so honouring the key changes nothing until someone sets it false.
    /// False is for deployments that migrate as a deliberate, separate step; the app still starts,
    /// but says loudly that the schema is whatever was already there.
    /// </summary>
    public bool MigrateOnStartup => config.GetValue("Database:MigrateOnStartup", true);

    /// <summary>
    /// Refuses a DSN whose pooling settings would make a per-request session variable meaningless
    /// (step 18 item A-2). <see cref="Data.TenantScopeInterceptor"/> writes
    /// <c>rediensiam.org_id</c> once per connection checkout and relies on Npgsql clearing it on
    /// return; both of these turn that into a cross-tenant read:
    ///
    /// <list type="bullet">
    /// <item><c>No Reset On Close=true</c> suppresses the <c>DISCARD ALL</c> that clears the
    /// setting, so one tenant's scope serves whichever request rents the connection next.</item>
    /// <item><c>Multiplexing=true</c> interleaves commands from different logical connections over
    /// one physical session, so there is no per-request session to scope at all.</item>
    /// </list>
    ///
    /// A startup failure rather than a warning: both are performance flags somebody would add
    /// deliberately, and the damage they do is silent and cross-tenant.
    /// </summary>
    private static string RequirePerCheckoutSessionState(string dsn)
    {
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(dsn);
        if (parsed.NoResetOnClose)
            throw new InvalidOperationException(
                "ConnectionStrings:Default sets 'No Reset On Close=true'. Npgsql's DISCARD ALL on pool " +
                "return is what clears the rediensiam.org_id row-level-security scope; without it one " +
                "tenant's scope leaks into the next request that rents the connection.");
        if (parsed.Multiplexing)
            throw new InvalidOperationException(
                "ConnectionStrings:Default sets 'Multiplexing=true'. Multiplexed commands from different " +
                "logical connections share one physical session, so a per-request rediensiam.org_id " +
                "row-level-security scope cannot be honoured.");
        return dsn;
    }

    // ── Cache / Redis ─────────────────────────────────────────────────────────
    public string CacheConnectionString => config["Cache:ConnectionString"] ?? "localhost:6379,abortConnect=false";
    /// <summary>
    /// PEM bundle holding the root the cache's TLS certificate must chain to. Defaults to the path
    /// the chart is expected to mount the cert-manager CA at, so enabling cache TLS needs no
    /// application configuration; absent, the file is simply not there and nothing changes.
    /// See <see cref="CacheTls"/>.
    /// </summary>
    public string CacheTlsCaFile        => config["Cache:TlsCaFile"] ?? CacheTls.DefaultCaBundlePath;
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

    /// <summary>
    /// How long Hydra keeps an SSO session after a successful login, in minutes.
    ///
    /// <para>
    /// This used to be nothing at all: <c>AcceptLoginAsync</c> sent <c>remember = false</c>, so
    /// every authorization request needed the password again — refreshing the console asked for it,
    /// and so did opening a second application against the same identity provider. The symptom read
    /// like a cookie problem. Nothing had ever asked Hydra to remember anybody.
    /// </para>
    ///
    /// <para>
    /// Eight hours by default: long enough to be a working day, short enough that a shared machine
    /// does not carry the session into the next one. Clamped to a week, and <b>zero disables the
    /// session entirely</b> — a deployment that wants a password at every authorization can have
    /// it, as a decision rather than as an oversight. Signing out still ends it immediately, and so
    /// does every path that calls <c>RevokeSessionsAsync</c>.
    /// </para>
    /// </summary>
    public int    SsoSessionMinutes       => Math.Clamp(config.GetValue<int>("Security:SsoSessionMinutes", 480), 0, 10080);
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
    /// Empty value means no pepper (back-compat with existing hashes).
    /// This is pepper id 1 — see <see cref="Argon2PepperRing"/> for rotation.</summary>
    public string Argon2Pepper            => config["Security:Argon2Pepper"] ?? "";

    /// <summary>
    /// Optional multi-pepper list for Argon2 pepper rotation. Format <c>id:hex,id:hex,…</c>,
    /// <b>first entry active</b>, same convention as <see cref="EncryptionKeys"/>. When unset,
    /// <see cref="Argon2Pepper"/> is pepper id 1 (or, if empty, there is no pepper at all — id 0).
    /// Unlike ciphertexts, password hashes cannot be re-derived without the plaintext: rotation
    /// happens one login at a time. See <c>SECURITY-AUDIT-LOG.md</c> step 16 §5.
    /// </summary>
    public string? Argon2Peppers          => config["Security:Argon2Peppers"];
    public string PatPrefix               => config["Security:PatPrefix"] ?? "rediens_pat_";
    /// <summary>Upper bound on a service-account PAT's lifetime. A credential that never expires
    /// outlives every revocation path the deployment has.</summary>
    public int    MaxPatLifetimeDays      => Math.Clamp(config.GetValue<int>("Security:MaxPatLifetimeDays", 365), 1, 730);

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

    // ── Root encryption keys (S-10 key rotation) ──────────────────────────────
    /// <summary>
    /// Optional multi-root key list, enabling incremental rotation of the HKDF root.
    /// Format: <c>id:hex,id:hex,…</c> — <b>the first entry is the active key</b> (everything new
    /// is encrypted under it), every entry can decrypt. Ids are small positive integers and must
    /// never be reused for a different key. When unset, <see cref="TotpSecretEncryptionKey"/> is
    /// used as key id 1, which is byte-for-byte the pre-rotation behaviour.
    /// Ordering deliberately matches Ory Hydra's <c>secrets.system</c> convention.
    /// </summary>
    public string? EncryptionKeys => config["Security:EncryptionKeys"];

    private List<(int Id, byte[] Root)>? _roots;
    private List<(int Id, byte[] Root)> Roots => _roots ??= ParseRoots();

    /// <summary>Key id every new ciphertext is written under.</summary>
    public int ActiveEncryptionKeyId => Roots[0].Id;

    /// <summary>Every key id that can currently decrypt, active first.</summary>
    public IReadOnlyList<int> ConfiguredEncryptionKeyIds => [.. Roots.Select(r => r.Id)];

    /// <summary>True when any configured root is the all-zero dev placeholder. Checked at startup.</summary>
    public bool HasPlaceholderEncryptionKey => Roots.Any(r => Array.TrueForAll(r.Root, b => b == 0));

    private List<(int Id, byte[] Root)> ParseRoots()
    {
        if (string.IsNullOrWhiteSpace(EncryptionKeys))
            return [(Services.TotpEncryption.LegacyKeyId, Convert.FromHexString(TotpSecretEncryptionKey))];

        var parsed = new List<(int Id, byte[] Root)>();
        foreach (var entry in EncryptionKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = entry.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
                throw new InvalidOperationException(
                    "Security:EncryptionKeys entries must be 'id:hex' — e.g. '2:<64 hex>,1:<64 hex>' with the active key first.");
            if (!int.TryParse(entry[..sep], out var id) || id < 1)
                throw new InvalidOperationException("Security:EncryptionKeys key ids must be positive integers.");
            if (parsed.Exists(p => p.Id == id))
                throw new InvalidOperationException($"Security:EncryptionKeys contains duplicate key id {id}.");

            var hex = entry[(sep + 1)..];
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
                throw new InvalidOperationException(
                    $"Security:EncryptionKeys key id {id} must be exactly 64 hex characters (32 bytes). Generate one with: openssl rand -hex 32");
            parsed.Add((id, Convert.FromHexString(hex)));
        }
        return parsed;
    }

    // ── Per-purpose derived keys (HKDF-SHA256 from each root) ─────────────────
    // Each purpose gets an independent 32-byte subkey per root; compromise of one purpose
    // does not expose data encrypted under other purposes, and each purpose's ring can
    // decrypt under every configured root while encrypting only under the active one.
    private Services.KeyRing? _totpEncKey;
    private Services.KeyRing? _webhookEncKey;
    private Services.KeyRing? _smtpEncKey;
    private Services.KeyRing? _themeEncKey;
    private Services.KeyRing? _dataProtectionKey;
    private Services.KeyRing? _auditChainKey;
    private byte[]? _deviceFpKey;

    public Services.KeyRing TotpEncKey    => _totpEncKey    ??= DeriveRing("rediensiam-totp-secret-v1");
    public Services.KeyRing WebhookEncKey => _webhookEncKey ??= DeriveRing("rediensiam-webhook-secret-v1");
    public Services.KeyRing SmtpEncKey    => _smtpEncKey    ??= DeriveRing("rediensiam-smtp-password-v1");
    public Services.KeyRing ThemeEncKey   => _themeEncKey   ??= DeriveRing("rediensiam-theme-secret-v1");

    /// <summary>
    /// Wraps the DataProtection key ring before it is written to the cache — see
    /// <see cref="KeyRingProtection"/>. Its own purpose string, so a compromise of any other
    /// derived subkey does not yield the ability to mint session cookies, and the purpose is
    /// versioned because changing it would orphan every key already stored under the old one.
    /// </summary>
    public Services.KeyRing DataProtectionKey => _dataProtectionKey ??= DeriveRing("rediensiam-dataprotection-v1");

    /// <summary>
    /// HMAC key for the audit hash chain (<see cref="Data.AuditChain"/>). Its own purpose string,
    /// like <see cref="DataProtectionKey"/>, so no other derived subkey can forge a chain.
    ///
    /// <para>
    /// This is a <b>ring</b> rather than the active root alone, and that is the whole reason chain
    /// keying can survive rotation: every row records the key id its MAC was written under, so a
    /// rotated root re-keys new rows while every historic row stays verifiable under the root it
    /// was written with. Retiring a root does not corrupt history — it makes the rows under it
    /// <i>unverifiable</i>, which <see cref="Data.AuditChainStatus"/> reports as such rather than
    /// as tampering.
    /// </para>
    /// </summary>
    public Services.KeyRing AuditChainKey => _auditChainKey ??= DeriveRing("rediensiam-audit-chain-v1");

    /// <summary>
    /// HMAC key for device fingerprints. Not a ciphertext key — fingerprints are one-way and
    /// carry no key id, so this deliberately follows the <b>active</b> root only. Retiring a root
    /// therefore invalidates the new-device cache: users get one extra "new device" notification
    /// each. That is the intended trade; versioning fingerprints would buy nothing.
    /// </summary>
    public byte[] DeviceFpKey => _deviceFpKey ??= DeriveKey(Roots[0].Root, "rediensiam-device-fingerprint-v1");

    // ── Argon2 pepper ring (S-10) ─────────────────────────────────────────────
    private List<(int Id, byte[] Pepper)>? _pepperRing;

    /// <summary>
    /// Configured peppers, active first. Empty means "no pepper configured" (pepper id 0).
    /// A stored hash with no pepper marker is by definition pepper id 1 — the pre-rotation value.
    /// </summary>
    public IReadOnlyList<(int Id, byte[] Pepper)> Argon2PepperRing => _pepperRing ??= ParsePeppers();

    private List<(int Id, byte[] Pepper)> ParsePeppers()
    {
        if (string.IsNullOrWhiteSpace(Argon2Peppers))
            return string.IsNullOrEmpty(Argon2Pepper)
                ? []
                : [(1, Convert.FromHexString(Argon2Pepper))];

        var parsed = new List<(int Id, byte[] Pepper)>();
        foreach (var entry in Argon2Peppers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var sep = entry.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
                throw new InvalidOperationException(
                    "Security:Argon2Peppers entries must be 'id:hex' — e.g. '2:<64 hex>,1:<64 hex>' with the active pepper first.");
            if (!int.TryParse(entry[..sep], out var id) || id < 1)
                throw new InvalidOperationException("Security:Argon2Peppers pepper ids must be positive integers.");
            if (parsed.Exists(p => p.Id == id))
                throw new InvalidOperationException($"Security:Argon2Peppers contains duplicate pepper id {id}.");

            var hex = entry[(sep + 1)..];
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
                throw new InvalidOperationException(
                    $"Security:Argon2Peppers pepper id {id} must be exactly 64 hex characters (32 bytes). Generate one with: openssl rand -hex 32");
            parsed.Add((id, Convert.FromHexString(hex)));
        }
        return parsed;
    }

    private Services.KeyRing DeriveRing(string purpose) =>
        new(Roots[0].Id, Roots.ToDictionary(r => r.Id, r => DeriveKey(r.Root, purpose)));

    private static byte[] DeriveKey(byte[] root, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, info: Encoding.UTF8.GetBytes(purpose));

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
