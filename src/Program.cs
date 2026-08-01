using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Controllers;
using Prometheus;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Controllers;
using RediensIAM.Middleware;
using RediensIAM.Services;

var builder = WebApplication.CreateBuilder(args);

// Stateless runtime config: see Config/InstanceConfiguration.cs.
// Loads non-secret values from the instances DB table on top of env/appsettings.
builder.Configuration.AddInstanceConfiguration();

// ── AppConfig (single source of truth for all env/config keys) ────────────
builder.Services.AddSingleton<AppConfig>();
var appConfig = new AppConfig(builder.Configuration);

// Validate encryption key before DI is locked (uses builder.Environment, available pre-Build)
ValidateEncryptionKey(appConfig, builder.Environment);

// ── Host header filtering ──────────────────────────────────────────────────
// The chart ships AllowedHosts="*" on the grounds that Traefik filters hosts at the ingress,
// which turns off host filtering in the app entirely. Rather than trust that, derive the list
// from the URLs this deployment already declares as its own. Kubernetes probes send an explicit
// Host header matching App__PublicUrl, so they are covered. An operator who sets a real
// AllowedHosts list still wins — this only replaces the wildcard.
builder.Services.PostConfigure<Microsoft.AspNetCore.HostFiltering.HostFilteringOptions>(
    o => ReplaceWildcardAllowedHosts(o, appConfig));

// ── Database ───────────────────────────────────────────────────────────────
// The interceptor publishes the request's tenant scope as the rediensiam.org_id session variable
// the RLS policies read (S-5 phase 2). It is inert until the policies are applied, and applying
// them without it is an outage — see Data/TenantScopeInterceptor.cs.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TenantScopeInterceptor>();
builder.Services.AddDbContext<RediensIamDbContext>((sp, options) =>
    options.UseNpgsql(appConfig.ConnectionString)
           .AddInterceptors(sp.GetRequiredService<TenantScopeInterceptor>()),
    ServiceLifetime.Scoped);

// ── Redis / Dragonfly ──────────────────────────────────────────────────────
builder.Services.Configure<ForwardedHeadersOptions>(o => ConfigureForwardedHeaders(o, builder.Configuration, builder.Environment));

// ConnectAsync(string) validates the server certificate against the OS trust store and nothing in
// the connection string changes that, which is what blocked cache TLS in step 18. CacheTls pins to
// the mounted cluster CA instead; on a plaintext connection string it is a no-op.
var cacheOptions = CacheTls.BuildOptions(appConfig.CacheConnectionString, appConfig.CacheTlsCaFile, Console.Error.WriteLine);
var cacheMultiplexer = await ConnectionMultiplexer.ConnectAsync(cacheOptions);
builder.Services.AddSingleton<IConnectionMultiplexer>(cacheMultiplexer);
builder.Services.AddStackExchangeRedisCache(o =>
{
    // Its own options instance: the cache builds a second multiplexer from these.
    o.ConfigurationOptions = CacheTls.BuildOptions(appConfig.CacheConnectionString, appConfig.CacheTlsCaFile);
    o.InstanceName         = appConfig.CacheInstanceName;
});

// ── Data Protection — persist keys to Redis so pod restarts don't invalidate sessions ──
// The ring is what mints session cookies, and by default it is written to the cache in the clear.
// ProtectKeysWithRootKey encrypts it under a purpose-derived subkey of the HKDF root and refuses
// to read a key that arrived unencrypted — see Config/KeyRingProtection.cs. This is deliberately
// independent of cache TLS: TLS protects the wire, this protects the stored bytes.
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(cacheMultiplexer, "rediensiam:dataprotection:keys")
    .ProtectKeysWithRootKey(appConfig)
    .SetApplicationName("rediensiam");

// ── Session (for MFA state) — backed by Redis so it survives pod restarts ──
builder.Services.AddSession(o =>
{
    o.IdleTimeout = TimeSpan.FromMinutes(15);
    o.Cookie.HttpOnly = true;
    o.Cookie.IsEssential = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// ── HTTP Clients ───────────────────────────────────────────────────────────
builder.Services.AddHttpClient("hydra-admin").AddStandardResilienceHandler();
builder.Services.AddHttpClient("keto-read").AddStandardResilienceHandler();
builder.Services.AddHttpClient("keto-write").AddStandardResilienceHandler();
builder.Services.AddHttpClient("health", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient();
// The unnamed client fetches SAML IdP metadata (via ITfoxtec) and the HIBP range API — both
// operator- or tenant-named hosts. Redirects stay enabled: the connect callback vets each hop.
builder.Services.AddHttpClient(string.Empty)
    .ConfigurePrimaryHttpMessageHandler(() => WebhookUrlValidator.CreateSsrfSafeHandler(allowAutoRedirect: true));
builder.Services.AddMemoryCache();

// ── Services ───────────────────────────────────────────────────────────────
builder.Services.AddScoped<PasswordService>();
builder.Services.AddScoped<OtpCacheService>();
builder.Services.AddScoped<LoginRateLimiter>();
builder.Services.AddScoped<HydraService>();
builder.Services.AddScoped<KetoService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<BreachCheckService>();
builder.Services.AddScoped<PasswordPolicyService>();
builder.Services.AddScoped<LiveAuthorizationService>();
builder.Services.AddScoped<SamlService>();
builder.Services.AddSingleton(_ => System.Threading.Channels.Channel.CreateUnbounded<RediensIAM.Services.WebhookJob>());
builder.Services.AddSingleton<IWebhookQueue, RedisWebhookQueue>();
builder.Services.AddSingleton<IWebhookSsrfValidator, WebhookSsrfValidator>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddScoped<KeyRotationService>();
builder.Services.AddHostedService<WebhookDispatcherService>();
builder.Services.AddHostedService<AuditLogRetentionService>();
// Every client that dials a URL chosen by a tenant or by a remote provider gets the SSRF-safe
// handler: no redirects, and the reserved-range check runs on the address actually connected to
// rather than on a separate DNS lookup. See WebhookUrlValidator.CreateSsrfSafeHandler.
builder.Services.AddHttpClient("webhook")
    .ConfigurePrimaryHttpMessageHandler(() => WebhookUrlValidator.CreateSsrfSafeHandler());
builder.Services.AddHttpClient(SocialLoginService.NoRedirectClient)
    .ConfigurePrimaryHttpMessageHandler(() => WebhookUrlValidator.CreateSsrfSafeHandler());
builder.Services.AddScoped<PatService>();
builder.Services.AddSingleton<SocialLoginService>();

// ── Controller service bundles (reduce constructor param counts, S107) ────────
builder.Services.AddScoped<AuthCoreServices>();
builder.Services.AddScoped<AuthExtServices>();
builder.Services.AddScoped<AuthControllerServices>();
builder.Services.AddScoped<AccountControllerServices>();
builder.Services.AddScoped<OrgAdminServices>();

// ── WebAuthn / Passkeys ────────────────────────────────────────────────────
builder.Services.AddFido2(opts =>
{
    opts.ServerDomain            = appConfig.Domain;
    opts.ServerName              = "RediensIAM";
    opts.Origins                 = new HashSet<string> { appConfig.PublicUrl };
    opts.TimestampDriftTolerance = 300_000;
});

// ── Notification services ───────────────────────────────────────────────────
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<ISmsService, StubSmsService>();
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
        o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });
builder.Services.AddHealthChecks();

// ── OpenAPI / Swagger (admin port only) ────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title   = "RediensIAM API",
        Version = "v1",
        Description = "Identity & Access Management API"
    });
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Type         = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        In           = Microsoft.OpenApi.ParameterLocation.Header
    });
    c.AddSecurityRequirement(document => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);
});

// ── CORS ───────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminSpa", policy => policy
        .WithOrigins(appConfig.AdminSpaOrigin)
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

// ── Dual-port via Kestrel ──────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(appConfig.PublicPort);
    kestrel.ListenAnyIP(appConfig.AdminPort);
});

var app = builder.Build();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

if (appConfig.HasPlaceholderEncryptionKey)
    logger.LogWarning("WARNING: an encryption root is the default all-zero dev placeholder. Override via Security__TotpSecretEncryptionKey (or Security__EncryptionKeys) before production.");

if (logger.IsEnabled(LogLevel.Information))
    logger.LogInformation(
        "Encryption key ring: active key id {ActiveKeyId}, configured ids [{KeyIds}]; Argon2 pepper ids [{PepperIds}]",
        appConfig.ActiveEncryptionKeyId,
        string.Join(',', appConfig.ConfiguredEncryptionKeyIds),
        string.Join(',', appConfig.Argon2PepperRing.Select(p => p.Id)));

if (app.Environment.IsProduction())
    WarnOnNonHttpsProductionUrls(logger, appConfig);

// ── Ensure DB schema exists ─────────────────────────────────────────────────
await EnsureDbSchemaAsync(app);

static async Task EnsureDbSchemaAsync(WebApplication webApp)
{
    using var scope = webApp.Services.CreateScope();
    var db     = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database schema ready");
            break;
        }
        catch (Exception ex) when (attempt < 12)
        {
            logger.LogWarning(ex, "DB not ready (attempt {Attempt}/12), retrying in 5s", attempt);
            await Task.Delay(5000);
        }
        catch (Exception ex)
        {
            // Wrapped rather than rethrown bare: the bare throw left the retry count in the log
            // line only, so a crash report read without the logs lost the one fact that
            // distinguishes a slow database from a broken migration.
            logger.LogCritical(ex, "DB schema creation failed after 12 attempts — aborting startup");
            throw new InvalidOperationException(
                "Database schema creation failed after 12 attempts over roughly 60 seconds. The "
                + "retries would have continued had the database merely been unreachable, so "
                + "treat this as a migration failure rather than a connectivity one.", ex);
        }
    }
}

// ── Ensure admin SPA OAuth2 client registered with correct settings ────────
await EnsureHydraAdminClientAsync(app, appConfig);

// ── Bootstrap super admin ──────────────────────────────────────────────────
if (!string.IsNullOrEmpty(appConfig.BootstrapEmail) && !string.IsNullOrEmpty(appConfig.BootstrapPassword))
    await BootstrapSuperAdminAsync(app, appConfig);

static async Task BootstrapSuperAdminAsync(WebApplication webApp, AppConfig cfg)
{
    var log = webApp.Services.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var scope = webApp.Services.CreateScope();
            var bdb   = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
            var bketo = scope.ServiceProvider.GetRequiredService<KetoService>();
            var bpwd  = scope.ServiceProvider.GetRequiredService<PasswordService>();
            await EnsureBootstrapAdminAsync(bdb, bketo, bpwd, cfg.BootstrapEmail!, cfg.BootstrapPassword!, log);
            break;
        }
        catch (Exception ex) when (attempt < 12)
        {
            log.LogWarning(ex, "Bootstrap attempt {Attempt}/12 failed, retrying in 5s", attempt);
            await Task.Delay(5000);
        }
        catch (Exception ex) { log.LogWarning(ex, "Bootstrap super admin failed"); }
    }
}

static async Task EnsureHydraAdminClientAsync(WebApplication webApp, AppConfig cfg)
{
    var log = webApp.Services.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var scope = webApp.Services.CreateScope();
            var hydra = scope.ServiceProvider.GetRequiredService<HydraService>();
            await hydra.EnsureAdminSpaClientAsync(cfg.AdminSpaOrigin);
            if (log.IsEnabled(LogLevel.Information))
                log.LogInformation("Admin SPA OAuth2 client '{ClientId}' registered (token_endpoint_auth_method=none)", Roles.AdminClientId);
            return;
        }
        catch (Exception ex) when (attempt < 12)
        {
            log.LogWarning(ex, "Hydra not ready for admin client setup (attempt {Attempt}/12), retrying in 5s", attempt);
            await Task.Delay(5000);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to register admin SPA client with Hydra after 12 attempts");
        }
    }
}

static async Task EnsureBootstrapAdminAsync(
    RediensIamDbContext bdb, KetoService bketo, PasswordService bpwd,
    string bootstrapEmail, string bootstrapPassword, ILogger log)
{
    var email = bootstrapEmail.ToLowerInvariant();
    var systemList = await bdb.UserLists.FirstOrDefaultAsync(ul => ul.Name == "__system__");
    if (systemList == null)
    {
        systemList = new UserList { Id = Guid.NewGuid(), Name = "__system__", Immovable = true, CreatedAt = DateTimeOffset.UtcNow };
        bdb.UserLists.Add(systemList);
        await bdb.SaveChangesAsync();
    }
    if (!await bdb.Users.AnyAsync(u => u.Email == email))
    {
        var user = new User
        {
            Id = Guid.NewGuid(), UserListId = systemList.Id,
            Email = email, Username = email.Split('@')[0], Discriminator = "0000",
            EmailVerified = true, EmailVerifiedAt = DateTimeOffset.UtcNow,
            PasswordHash = bpwd.Hash(bootstrapPassword),
            Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        bdb.Users.Add(user);
        // Persist the user BEFORE granting super_admin in Keto. The reverse order leaves an
        // orphaned super_admin tuple pointing at a user id that was never committed.
        await bdb.SaveChangesAsync();
        await bketo.WriteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Bootstrap super admin created: {Email}", email);
        log.LogWarning("Bootstrap complete. Remove IAM_BOOTSTRAP_PASSWORD from environment variables.");
    }
}

// ── Middleware pipeline ────────────────────────────────────────────────────
app.UseMiddleware<AppExceptionMiddleware>();
app.UseForwardedHeaders();

// ── Security headers ───────────────────────────────────────────────────────
// The admin console runs on its own origin and fetches {issuer}/.well-known/openid-configuration
// before it can redirect, so connect-src has to name the issuer origin explicitly — 'self' can
// never cover it. Resolved once at startup from the same value /admin/config hands the SPA.
var issuerOrigin = Uri.TryCreate(appConfig.PublicUrl, UriKind.Absolute, out var issuerUri)
    ? issuerUri.GetLeftPart(UriPartial.Authority)
    : "";
app.Use((ctx, next) => { AddSecurityHeaders(ctx, issuerOrigin); return next(); });

// ── Swagger UI — admin port only ───────────────────────────────────────────
app.UseWhen(ctx => ctx.Connection.LocalPort == appConfig.AdminPort, branch =>
{
    branch.UseSwagger();
    branch.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "RediensIAM v1"));
});

// ── Prometheus HTTP request metrics ───────────────────────────────────────
app.UseHttpMetrics();

app.UseSession();
app.UseCors("AdminSpa");
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.MapHealthChecks("/health");

// Protect account/project/org/internal/manage/system routes — admin SPA loads without auth (handles PKCE itself)
// /admin/system is always auth-gated (no browser SPA navigation hits it, only API calls)
var protectedPrefixes = new[] { "/account", "/project", "/org", "/internal", "/service-accounts", "/api", "/admin/system", "/auth/oauth2/link" };
app.UseWhen(
    ctx => protectedPrefixes.Any(p => ctx.Request.Path.StartsWithSegments(p)),
    branch => branch.UseMiddleware<GatewayAuthMiddleware>());

// Validate admin API Bearer tokens.
//
// The SPA is served from /admin/* and its browser navigations carry no Authorization header, so
// they have to pass. The condition used to be "GET without an Authorization header", which made
// every unauthenticated admin GET reach its controller and depend on that controller carrying
// [RequireManagementLevel]. UseRouting has already run here, so the request can be told apart
// properly: if it resolved to a controller action it is an API call and needs a token; if it did
// not, it is a static asset or the SPA fallback.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/admin")
        && !ctx.Request.Path.Equals("/admin/config")
        && (ctx.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() != null
            || ctx.Request.Method != HttpMethods.Get),
    branch => branch.UseMiddleware<GatewayAuthMiddleware>());

// Public — no auth required; must be a minimal endpoint to bypass [RequireManagementLevel] on SystemAdminController
app.MapGet("/admin/config", (AppConfig cfg) => Results.Json(
    new { hydra_url = cfg.PublicUrl, client_id = Roles.AdminClientId, redirect_uri = $"{cfg.AdminSpaOrigin}/admin/callback" },
    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));

app.MapControllers();

// ── Prometheus scrape endpoint — admin port only ───────────────────────────
app.MapMetrics("/metrics")
   .RequireHost($"*:{appConfig.AdminPort}");

app.MapFallback("/admin/{**path}", async (string path, HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(
        Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin", "index.html"));
});

app.MapFallbackToFile("index.html");

await app.RunAsync();

// Body of the HostFilteringOptions PostConfigure above — extracted verbatim. Runs at
// PostConfigure time, not registration time, so an operator-supplied AllowedHosts list still wins.
static void ReplaceWildcardAllowedHosts(Microsoft.AspNetCore.HostFiltering.HostFilteringOptions o, AppConfig cfg)
{
    if (!o.AllowedHosts.Contains("*")) return;
    var derived = new[] { cfg.PublicUrl, cfg.AdminSpaOrigin }
        .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null)
        .Append(cfg.Domain)
        .Where(h => !string.IsNullOrWhiteSpace(h))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (derived.Count > 0) o.AllowedHosts = derived!;
}

// The two production URL warnings from the startup block — same messages, same levels, same order.
static void WarnOnNonHttpsProductionUrls(ILogger log, AppConfig cfg)
{
    if (!cfg.PublicUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        log.LogError("SECURITY: App__PublicUrl is not HTTPS in production ({Url}). OAuth2 tokens, session cookies, and redirects will be insecure.", cfg.PublicUrl);
    if (!cfg.AdminSpaOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        log.LogWarning("WARNING: App__AdminSpaOrigin is not HTTPS in production ({Url}).", cfg.AdminSpaOrigin);
}

static void ValidateEncryptionKey(AppConfig cfg, IWebHostEnvironment env)
{
    if (string.IsNullOrWhiteSpace(cfg.EncryptionKeys))
    {
        var encKeyVal = cfg.TotpSecretEncryptionKey;
        if (encKeyVal.Length != 64 || !encKeyVal.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "Security:TotpSecretEncryptionKey must be exactly 64 hex characters (32 bytes). " +
                "Generate one with: openssl rand -hex 32");
    }
    // Forces the key ring to parse: malformed Security:EncryptionKeys must fail at startup,
    // not on the first TOTP decrypt. Also forces the pepper ring for the same reason.
    _ = cfg.ConfiguredEncryptionKeyIds;
    _ = cfg.Argon2PepperRing;
    if (cfg.HasPlaceholderEncryptionKey && env.IsProduction())
        throw new InvalidOperationException(
            "The encryption root must not be the default all-zero dev placeholder in production.");
}

static void AddSecurityHeaders(HttpContext ctx, string issuerOrigin)
{
    ctx.Response.Headers.XContentTypeOptions   = "nosniff";
    ctx.Response.Headers["Referrer-Policy"]    = "strict-origin-when-cross-origin";
    ctx.Response.Headers.XXSSProtection        = "0";
    ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";
    if (ctx.Request.IsHttps)
        ctx.Response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
    ctx.Response.Headers.XFrameOptions = "DENY";
    // default-src is the fallback for every directive that is not named. Omitting it left
    // connect-src, img-src, font-src and friends wide open on the admin policy.
    // base-uri and form-action are not covered by default-src and must be set explicitly.
    //
    // style-src carries 'unsafe-inline' on both branches. Neither SPA can do without it: Radix
    // (via react-style-singleton) injects a <style> element on every dialog open, and the login
    // page renders the tenant's custom_css into one. Script injection stays refused — script-src
    // is 'self' with no inline escape — and the CSS sink itself is guarded server-side by
    // LoginThemeValidator, which is what makes widening this safe (C-6).
    var issuerConnect = string.IsNullOrEmpty(issuerOrigin) ? "'self'" : $"'self' {issuerOrigin}";
    ctx.Response.Headers.ContentSecurityPolicy = ctx.Request.Path.StartsWithSegments("/admin")
        ? "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
          $"font-src 'self'; img-src 'self' data:; connect-src {issuerConnect}; " +
          "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self';"
        // The login page renders tenant branding: a project logo and social-provider icons, both
        // remote HTTPS URLs the operator does not control. Images execute nothing.
        : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
          "font-src 'self'; img-src 'self' data: https:; connect-src 'self'; " +
          "object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self';";
}

// Configure forwarded-headers: honour X-Forwarded-* only from operator-trusted proxies.
// App__TrustedProxies (CSV of CIDRs) overrides the defaults. In production this MUST be
// set explicitly — silently trusting RFC1918 means any pod in a multi-tenant cluster can
// spoof X-Forwarded-For and bypass per-IP rate limiting / IP allowlists.
static void ConfigureForwardedHeaders(ForwardedHeadersOptions o, IConfiguration cfg, IWebHostEnvironment env)
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    var trusted = cfg["App:TrustedProxies"];
    if (!string.IsNullOrWhiteSpace(trusted))
    {
        if (ApplyTrustedProxiesFromConfig(o, trusted) == 0)
            throw new InvalidOperationException("App__TrustedProxies is set but no valid CIDR entries were parsed.");
        return;
    }
    if (env.IsProduction())
    {
        throw new InvalidOperationException(
            "App__TrustedProxies must be set explicitly in Production. " +
            "Silently trusting RFC1918 ranges allows any in-cluster pod to spoof X-Forwarded-For and bypass IP-based controls.");
    }
    AddDefaultTrustedNetworks(o);
}

static int ApplyTrustedProxiesFromConfig(ForwardedHeadersOptions o, string trusted)
{
    var parsed = 0;
    foreach (var cidr in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (TryParseCidr(cidr, out var network))
        {
            o.KnownIPNetworks.Add(network);
            parsed++;
        }
        else
        {
            Console.Error.WriteLine($"WARNING: App__TrustedProxies entry '{cidr}' is not a valid CIDR — ignored.");
        }
    }
    return parsed;
}

static bool TryParseCidr(string cidr, out System.Net.IPNetwork network)
{
    var parts = cidr.Split('/');
    if (parts.Length == 2
        && System.Net.IPAddress.TryParse(parts[0], out var ip)
        && int.TryParse(parts[1], out var prefix))
    {
        network = new System.Net.IPNetwork(ip, prefix);
        return true;
    }
    network = default;
    return false;
}

static void AddDefaultTrustedNetworks(ForwardedHeadersOptions o)
{
    // Well-known private + loopback ranges (RFC1918 + RFC5735). Not routable on the
    // public internet, used by k3s/k8s pod networks.
    //
    // S1313 asks whether hardcoding an IP is safe. Here it is the only correct option: these
    // literals are the RFC's own definition of a private network, not a deployment's address.
    // Making them configurable would let an operator declare the public internet private and
    // silently trust X-Forwarded-For from anywhere — the exact failure App__TrustedProxies
    // refuses to start without. That value is what an operator sets; this is the constant.
#pragma warning disable S1313 // RFC-defined ranges, deliberately not configurable
    var defaults = new (string Address, int Prefix)[]
    {
        ("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("127.0.0.0", 8),
    };
#pragma warning restore S1313
    foreach (var (address, prefix) in defaults)
        o.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse(address), prefix));
}

/// <summary>
/// Declared partial and public solely so <c>WebApplicationFactory&lt;Program&gt;</c> in the
/// integration test project can name it. Nothing references it at runtime.
/// </summary>
public partial class Program
{
    protected Program() { }
}
