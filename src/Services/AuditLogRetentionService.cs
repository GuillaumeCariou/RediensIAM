using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;

namespace RediensIAM.Services;

public class AuditLogRetentionService(
    IServiceScopeFactory scopeFactory,
    AppConfig appConfig,
    ILogger<AuditLogRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PurgeExpiredLogsAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Audit log retention purge failed");
            }

            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }

    private async Task PurgeExpiredLogsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

        // Purge per-org logs using the org's own retention setting (falling back to global)
        var orgs = await db.Organisations.AsNoTracking()
            .Select(o => new { o.Id, o.AuditRetentionDays })
            .ToListAsync(ct);

        int total = 0;
        foreach (var org in orgs)
        {
            var days = org.AuditRetentionDays ?? appConfig.AuditRetentionDays;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            var deleted = await db.AuditLogs
                .Where(a => a.OrgId == org.Id && a.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);
            total += deleted;
        }

        // Purge system-level logs (OrgId == null) using the global retention setting
        var systemCutoff = DateTimeOffset.UtcNow.AddDays(-appConfig.AuditRetentionDays);
        total += await db.AuditLogs
            .Where(a => a.OrgId == null && a.CreatedAt < systemCutoff)
            .ExecuteDeleteAsync(ct);

        if (total > 0)
            logger.LogInformation("Audit log retention: purged {Count} expired entries", total);
    }
}
