using Microsoft.AspNetCore.HttpOverrides;
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
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// ── Services ───────────────────────────────────────────────────────────────
builder.Services.AddScoped<PasswordService>();
builder.Services.AddScoped<OtpCacheService>();
builder.Services.AddScoped<LoginRateLimiter>();
builder.Services.AddScoped<HydraService>();
builder.Services.AddScoped<KetoService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<PatService>();
builder.Services.AddScoped<SocialLoginService>();
builder.Services.AddHttpContextAccessor();

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

// ── Ensure DB schema exists ─────────────────────────────────────────────────
{
    using var scope = app.Services.CreateScope();
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
            ");
            logger.LogInformation("Database schema ready");
            break;
        }
        catch (Exception ex) when (attempt < 12)
        {
            logger.LogWarning("DB not ready (attempt {Attempt}/12), retrying in 5s: {Message}", attempt, ex.Message);
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
{
    var blog = app.Services.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var bscope = app.Services.CreateScope();
            var bdb    = bscope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
            var bketo  = bscope.ServiceProvider.GetRequiredService<KetoService>();
            var bpwd   = bscope.ServiceProvider.GetRequiredService<PasswordService>();
            var email  = appConfig.BootstrapEmail.ToLowerInvariant();

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
                    PasswordHash = bpwd.Hash(appConfig.BootstrapPassword),
                    Active = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                };
                bdb.Users.Add(user);
                await bketo.WriteRelationTupleAsync(Roles.KetoSystemNamespace, Roles.KetoSystemObject, Roles.KetoSuperAdminRelation, $"user:{user.Id}");
                await bdb.SaveChangesAsync();
                blog.LogInformation("Bootstrap super admin created: {Email}", email);
            }
            break;
        }
        catch (Exception ex) when (attempt < 12)
        {
            blog.LogWarning("Bootstrap attempt {Attempt}/12 failed, retrying in 5s: {Message}", attempt, ex.Message);
            await Task.Delay(5000);
        }
        catch (Exception ex) { blog.LogWarning(ex, "Bootstrap super admin failed"); }
    }
}

// ── Middleware pipeline ────────────────────────────────────────────────────
app.UseMiddleware<AppExceptionMiddleware>();
app.UseForwardedHeaders();
app.UseSession();
app.UseCors("AdminSpa");
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.MapHealthChecks("/health");

// Protect account/project/org/internal routes — admin SPA loads without auth (handles PKCE itself)
var protectedPrefixes = new[] { "/account", "/project", "/org", "/internal", "/service-accounts" };
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

// Block admin/internal on public port
app.Use(async (ctx, next) =>
{
    var isAdminRoute = ctx.Request.Path.StartsWithSegments("/admin") || ctx.Request.Path.StartsWithSegments("/internal");
    if (isAdminRoute && ctx.Connection.LocalPort == appConfig.PublicPort)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    await next(ctx);
});

// Public — no auth required; must be a minimal endpoint to bypass [RequireManagementLevel] on SystemAdminController
app.MapGet("/admin/config", (AppConfig cfg) => Results.Json(
    new { hydra_url = cfg.PublicUrl, client_id = Roles.AdminClientId, redirect_uri = $"{cfg.PublicUrl}/admin/callback" },
    new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower }));

app.MapControllers();

// Admin SPA fallback (client-side routing)
app.MapFallback("/admin/{**path}", async ctx =>
{
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(
        Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "admin", "index.html"));
});

// Login SPA fallback
app.MapFallbackToFile("index.html");

app.Run();

// Expose Program to integration test project
public partial class Program { }
