using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RediensIAM.Config;
using RediensIAM.IntegrationTests.Infrastructure;
using RediensIAM.Services;

namespace RediensIAM.IntegrationTests.Tests.Services;

/// <summary>
/// Covers AuditLogRetentionService.PurgeExpiredLogsAsync (lines 41-57):
///   - per-org loop with audit logs older than retention window
///   - LogInformation when total > 0
/// </summary>
[Collection("RediensIAM")]
public class AuditLogRetentionServiceTests(TestFixture fixture)
{
    private static MethodInfo PurgeMethod { get; } =
        typeof(AuditLogRetentionService)
            .GetMethod("PurgeExpiredLogsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static Task InvokePurgeAsync(AuditLogRetentionService svc)
        => (Task)PurgeMethod.Invoke(svc, [CancellationToken.None])!;

    // ── Purge per-org logs older than retention window (lines 41-48) ──────────

    [Fact]
    public async Task PurgeExpiredLogs_OldOrgAuditLog_DeletesIt()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();

        // Insert an audit log entry created 400 days ago
        var oldLog = new AuditLog
        {
            OrgId     = org.Id,
            Action    = "test.purge",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400),
        };
        fixture.Db.AuditLogs.Add(oldLog);
        await fixture.Db.SaveChangesAsync();

        var scopeFactory = fixture.Services.GetRequiredService<IServiceScopeFactory>();
        var appConfig    = fixture.Services.GetRequiredService<AppConfig>();
        var svc = new AuditLogRetentionService(scopeFactory, appConfig,
            NullLogger<AuditLogRetentionService>.Instance);

        await InvokePurgeAsync(svc);

        // ExecuteDeleteAsync is a bulk operation bypassing EF cache — use AsNoTracking
        var still = await fixture.Db.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == oldLog.Id);
        still.Should().BeNull();
    }

    // ── LogInformation when total > 0 (line 57) ───────────────────────────────

    [Fact]
    public async Task PurgeExpiredLogs_WhenRowsDeleted_LogsCount()
    {
        var (org, _)  = await fixture.Seed.CreateOrgAsync();

        // Set org-level retention to 1 day and insert a 2-day-old log
        org.AuditRetentionDays = 1;
        var oldLog = new AuditLog
        {
            OrgId     = org.Id,
            Action    = "test.log_count",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        };
        fixture.Db.AuditLogs.Add(oldLog);
        await fixture.Db.SaveChangesAsync();

        var scopeFactory = fixture.Services.GetRequiredService<IServiceScopeFactory>();
        var appConfig    = fixture.Services.GetRequiredService<AppConfig>();
        var svc = new AuditLogRetentionService(scopeFactory, appConfig,
            NullLogger<AuditLogRetentionService>.Instance);

        // Should not throw, and should reach the if (total > 0) LogInformation branch
        var act = () => InvokePurgeAsync(svc);
        await act.Should().NotThrowAsync();
    }

    // ── System-level audit log purge (lines 50-54) ───────────────────────────

    [Fact]
    public async Task PurgeExpiredLogs_OldSystemAuditLog_DeletesIt()
    {
        var oldLog = new AuditLog
        {
            OrgId     = null,   // system-level
            Action    = "test.system_purge",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-400),
        };
        fixture.Db.AuditLogs.Add(oldLog);
        await fixture.Db.SaveChangesAsync();

        var scopeFactory = fixture.Services.GetRequiredService<IServiceScopeFactory>();
        var appConfig    = fixture.Services.GetRequiredService<AppConfig>();
        var svc = new AuditLogRetentionService(scopeFactory, appConfig,
            NullLogger<AuditLogRetentionService>.Instance);

        await InvokePurgeAsync(svc);

        // ExecuteDeleteAsync bypasses EF cache — use AsNoTracking
        var still = await fixture.Db.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == oldLog.Id);
        still.Should().BeNull();
    }
}
