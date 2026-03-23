using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using RediensIAM.Data;
using RediensIAM.Entities;
using RediensIAM.Middleware;
using RediensIAM.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ──────────────────────────────────────────────────────────
var config = builder.Configuration;
var publicPort = config.GetValue<int>("IAM_PUBLIC_PORT", 5000);
var adminPort = config.GetValue<int>("IAM_ADMIN_PORT", 5001);
var adminPath = config["IAM_ADMIN_PATH"] ?? "/admin";

// ── Database ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<RediensIamDbContext>(options =>
    options.UseNpgsql(
        config.GetConnectionString("Default") ?? "Host=localhost;Database=rediensiam;Username=iam;Password=changeme"),
    ServiceLifetime.Scoped);

// ── Redis / Dragonfly ──────────────────────────────────────────────────────
var redisConn = config["Cache:ConnectionString"] ?? "localhost:6379,abortConnect=false";
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redisConn));
builder.Services.AddStackExchangeRedisCache(o =>
{
    o.Configuration = redisConn;
    o.InstanceName = config["Cache:InstanceName"] ?? "rediensiam:";
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
builder.Services.AddScoped<TotpEncryptionService>();
builder.Services.AddScoped<OtpCacheService>();
builder.Services.AddScoped<LoginRateLimiter>();
builder.Services.AddSingleton<HydraJwksCache>();
builder.Services.AddScoped<HydraAdminService>();
builder.Services.AddScoped<KetoService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<PatIntrospectionService>();
builder.Services.AddScoped<PatGenerationService>();
builder.Services.AddScoped<RoleAssignmentService>();
builder.Services.AddScoped<ImpersonationService>();
builder.Services.AddHttpContextAccessor();

// ── WebAuthn / Passkeys ────────────────────────────────────────────────────
builder.Services.AddFido2(opts =>
{
    opts.ServerDomain = config["App:Domain"]
        ?? throw new InvalidOperationException("App:Domain configuration is required");
    opts.ServerName   = "RediensIAM";
    opts.Origins      = new HashSet<string>
    {
        config["App:PublicUrl"]
            ?? throw new InvalidOperationException("App:PublicUrl configuration is required")
    };
    opts.TimestampDriftTolerance = 300_000;
});

// ── Notification services ───────────────────────────────────────────────────
if (!string.IsNullOrEmpty(config["Smtp:Host"]))
    builder.Services.AddScoped<IEmailService, SmtpEmailService>();
else
    builder.Services.AddScoped<IEmailService, StubEmailService>();
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
        .WithOrigins(config["App:AdminSpaOrigin"] ?? "http://localhost:5001")
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

// ── Dual-port via Kestrel ──────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(publicPort);
    kestrel.ListenAnyIP(adminPort);
});

var app = builder.Build();

// ── Ensure DB schema exists ─────────────────────────────────────────────────
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
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
var bootstrapEmail = config["IAM_BOOTSTRAP_EMAIL"];
var bootstrapPassword = config["IAM_BOOTSTRAP_PASSWORD"];
if (!string.IsNullOrEmpty(bootstrapEmail) && !string.IsNullOrEmpty(bootstrapPassword))
{
    var blog = app.Services.GetRequiredService<ILogger<Program>>();
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var bscope = app.Services.CreateScope();
            var bdb = bscope.ServiceProvider.GetRequiredService<RediensIamDbContext>();
            var bketo = bscope.ServiceProvider.GetRequiredService<KetoService>();
            var bpwd = bscope.ServiceProvider.GetRequiredService<PasswordService>();
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
                await bketo.WriteRelationTupleAsync("System", "rediensiam", "super_admin", $"user:{user.Id}");
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
var protectedPrefixes = new[] { "/account", "/project", "/org", "/internal" };
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
    if (isAdminRoute && ctx.Connection.LocalPort == publicPort)
    {
        ctx.Response.StatusCode = 404;
        return;
    }
    await next(ctx);
});

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
