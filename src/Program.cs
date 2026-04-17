using Microsoft.AspNetCore.HttpOverrides;
using Prometheus;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using RediensIAM.Config;
using RediensIAM.Data;
using RediensIAM.Data.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

var builder = WebApplication.CreateBuilder(args);

// ── AppConfig (single source of truth for all env/config keys) ────────────
builder.Services.AddSingleton<AppConfig>();
var appConfig = new AppConfig(builder.Configuration);

// Validate encryption key before DI is locked (uses builder.Environment, available pre-Build)
if (appConfig.TotpSecretEncryptionKey == new string('0', 64) && builder.Environment.IsProduction())
    throw new InvalidOperationException("TotpSecretEncryptionKey must not be the default all-zero value in production.");

// ── Database ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<RediensIamDbContext>(options =>
    options.UseNpgsql(appConfig.ConnectionString),
    ServiceLifetime.Scoped);

// ── Redis / Dragonfly ──────────────────────────────────────────────────────
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(appConfig.CacheConnectionString));
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration  = appConfig.CacheConnectionString;
    o.InstanceName   = appConfig.CacheInstanceName;
});

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
builder.Services.AddHttpClient("hydra-admin");
builder.Services.AddHttpClient("keto-read");
builder.Services.AddHttpClient("keto-write");
builder.Services.AddHttpClient("health", c => c.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// ── Services ───────────────────────────────────────────────────────────────
builder.Services.AddScoped<PasswordService>();
builder.Services.AddScoped<OtpCacheService>();
builder.Services.AddScoped<LoginRateLimiter>();
builder.Services.AddScoped<HydraService>();
builder.Services.AddScoped<KetoService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<BreachCheckService>();
builder.Services.AddScoped<SamlService>();
builder.Services.AddSingleton(_ => System.Threading.Channels.Channel.CreateUnbounded<RediensIAM.Services.WebhookJob>());
builder.Services.AddScoped<WebhookService>();
builder.Services.AddHostedService<WebhookDispatcherService>();
builder.Services.AddHostedService<AuditLogRetentionService>();
builder.Services.AddHttpClient("webhook");
builder.Services.AddScoped<PatService>();
builder.Services.AddScoped<SocialLoginService>();
builder.Services.AddHttpContextAccessor();

// ── Controller service bundles (reduce constructor param counts, S107) ────────
builder.Services.AddScoped<AuthCoreServices>();
builder.Services.AddScoped<AuthExtServices>();
builder.Services.AddScoped<AuthControllerServices>();
builder.Services.AddScoped<AccountControllerServices>();
builder.Services.AddScoped<OrgAdminServices>();
builder.Services.AddScoped<ManagedApiServices>();

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

if (appConfig.TotpSecretEncryptionKey == new string('0', 64))
    logger.LogWarning("WARNING: TotpSecretEncryptionKey is the default all-zero dev key. Do not use in production.");

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
            await db.Database.EnsureCreatedAsync();
            // Idempotent schema additions for incremental releases (EnsureCreatedAsync won't update existing DBs)
            // Column names must be quoted PascalCase to match EF Core / Npgsql defaults.
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS org_smtp_configs (
                    ""Id""          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""OrgId""       UUID NOT NULL UNIQUE REFERENCES organisations(""Id"") ON DELETE CASCADE,
                    ""Host""        TEXT NOT NULL,
                    ""Port""        INTEGER NOT NULL DEFAULT 587,
                    ""StartTls""    BOOLEAN NOT NULL DEFAULT true,
                    ""Username""    TEXT,
                    ""PasswordEnc"" TEXT,
                    ""FromAddress"" TEXT NOT NULL,
                    ""FromName""    TEXT NOT NULL,
                    ""CreatedAt""   TIMESTAMPTZ NOT NULL DEFAULT now(),
                    ""UpdatedAt""   TIMESTAMPTZ NOT NULL DEFAULT now()
                );
                ALTER TABLE projects ADD COLUMN IF NOT EXISTS ""EmailFromName"" TEXT;
                ALTER TABLE projects ADD COLUMN IF NOT EXISTS ""RequireMfa"" BOOLEAN NOT NULL DEFAULT false;
                ALTER TABLE projects ADD COLUMN IF NOT EXISTS ""IpAllowlist"" JSONB NOT NULL DEFAULT '[]';
                ALTER TABLE projects ADD COLUMN IF NOT EXISTS ""CheckBreachedPasswords"" BOOLEAN NOT NULL DEFAULT false;
                ALTER TABLE projects ADD COLUMN IF NOT EXISTS ""AllowedScopes"" TEXT[] NOT NULL DEFAULT '{}';
                ALTER TABLE organisations ADD COLUMN IF NOT EXISTS ""AuditRetentionDays"" INTEGER;
                ALTER TABLE users ADD COLUMN IF NOT EXISTS ""NewDeviceAlertsEnabled"" BOOLEAN NOT NULL DEFAULT true;
                ALTER TABLE users ALTER COLUMN ""PasswordHash"" DROP NOT NULL;

                CREATE TABLE IF NOT EXISTS saml_idp_configs (
                    ""Id""                      UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""ProjectId""               UUID         NOT NULL REFERENCES projects(""Id"") ON DELETE CASCADE,
                    ""EntityId""                TEXT         NOT NULL,
                    ""MetadataUrl""             TEXT,
                    ""SsoUrl""                  TEXT,
                    ""CertificatePem""          TEXT,
                    ""EmailAttributeName""      TEXT         NOT NULL DEFAULT 'email',
                    ""DisplayNameAttributeName"" TEXT,
                    ""JitProvisioning""         BOOLEAN      NOT NULL DEFAULT true,
                    ""DefaultRoleId""           UUID         REFERENCES roles(""Id"") ON DELETE SET NULL,
                    ""Active""                  BOOLEAN      NOT NULL DEFAULT true,
                    ""CreatedAt""               TIMESTAMPTZ  NOT NULL DEFAULT now(),
                    ""UpdatedAt""               TIMESTAMPTZ  NOT NULL DEFAULT now()
                );
                CREATE TABLE IF NOT EXISTS webhooks (
                    ""Id""        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""OrgId""     UUID REFERENCES organisations(""Id"") ON DELETE CASCADE,
                    ""ProjectId"" UUID REFERENCES projects(""Id"") ON DELETE CASCADE,
                    ""Url""       TEXT NOT NULL,
                    ""SecretEnc"" TEXT NOT NULL DEFAULT '',
                    ""Events""    JSONB NOT NULL DEFAULT '[]',
                    ""Active""    BOOLEAN NOT NULL DEFAULT true,
                    ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT now()
                );
                CREATE TABLE IF NOT EXISTS webhook_deliveries (
                    ""Id""           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ""WebhookId""    UUID NOT NULL REFERENCES webhooks(""Id"") ON DELETE CASCADE,
                    ""Event""        TEXT NOT NULL,
                    ""Payload""      JSONB NOT NULL DEFAULT '{}',
                    ""StatusCode""   INTEGER,
                    ""ErrorMessage"" TEXT,
                    ""AttemptCount"" INTEGER NOT NULL DEFAULT 0,
                    ""DeliveredAt""  TIMESTAMPTZ,
                    ""CreatedAt""    TIMESTAMPTZ NOT NULL DEFAULT now()
                );
            ");
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
            logger.LogError(ex, "DB schema creation failed");
        }
    }
}

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
        await bketo.WriteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");
        await bdb.SaveChangesAsync();
        if (log.IsEnabled(LogLevel.Information))
            log.LogInformation("Bootstrap super admin created: {Email}", email);
        log.LogWarning("Bootstrap complete. Remove IAM_BOOTSTRAP_PASSWORD from environment variables.");
    }
}

// ── Middleware pipeline ────────────────────────────────────────────────────
app.UseMiddleware<AppExceptionMiddleware>();
app.UseForwardedHeaders();

// ── Security headers ───────────────────────────────────────────────────────
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions  = "nosniff";
    ctx.Response.Headers["Referrer-Policy"]   = "strict-origin-when-cross-origin";
    ctx.Response.Headers.XXSSProtection       = "0";
    ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

    // X-Frame-Options: skip for /preview — the admin SPA loads it in an iframe
    if (!ctx.Request.Path.StartsWithSegments("/preview"))
        ctx.Response.Headers.XFrameOptions = "DENY";

    // CSP: admin SPA gets a relaxed policy allowing inline styles; login SPA gets a strict policy
    if (ctx.Request.Path.StartsWithSegments("/admin"))
        ctx.Response.Headers.ContentSecurityPolicy =
            "script-src 'self'; style-src 'self'; object-src 'none'; frame-ancestors 'none';";
    else
        ctx.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none';";

    await next();
});

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
var protectedPrefixes = new[] { "/account", "/project", "/org", "/internal", "/service-accounts", "/api/manage", "/admin/system", "/auth/oauth2/link" };
app.UseWhen(
    ctx => protectedPrefixes.Any(p => ctx.Request.Path.StartsWithSegments(p)),
    branch => branch.UseMiddleware<GatewayAuthMiddleware>());

// Validate admin API Bearer tokens.
// GET without Authorization is allowed through (browser SPA navigations, controllers still check Claims).
// All mutating verbs (POST/PATCH/DELETE/PUT) always require a valid Bearer token.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/admin")
        && !ctx.Request.Path.Equals("/admin/config")
        && (ctx.Request.Headers.ContainsKey("Authorization") || ctx.Request.Method != HttpMethods.Get),
    branch => branch.UseMiddleware<GatewayAuthMiddleware>());

// Public — no auth required; must be a minimal endpoint to bypass [RequireManagementLevel] on SystemAdminController
app.MapGet("/admin/config", (AppConfig cfg) => Results.Json(
    new { hydra_url = cfg.PublicUrl, client_id = Roles.AdminClientId, redirect_uri = $"{cfg.AdminSpaOrigin}/admin/callback" },
    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));

app.MapControllers();

// ── Prometheus scrape endpoint — admin port only ───────────────────────────
app.MapMetrics("/metrics")
   .RequireHost($"*:{appConfig.AdminPort}");

// Admin SPA fallback (client-side routing)
app.MapFallback("/admin/{**path}", async (string path, HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(
        Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin", "index.html"));
});

// Login SPA fallback
app.MapFallbackToFile("index.html");

await app.RunAsync();

// Expose Program to integration test project
public partial class Program
{
    protected Program() { }
}
