using Microsoft.EntityFrameworkCore;
using RediensIAM.Config;
using RediensIAM.Data;

namespace RediensIAM.Services;

public class AuditLogRetentionService(
    IServiceScopeFactory scopeFactory,
    AppConfig appConfig,
    ILogger<AuditLogRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeExpiredLogsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Audit log retention purge failed");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task PurgeExpiredLogsAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

        // Purge per-org logs using the org's own retention setting (falling back to global)
        var orgs = await db.Organisations.AsNoTracking()
            .Select(o => new { o.Id, o.AuditRetentionDays })
            .ToListAsync(stoppingToken);

        int total = 0;
        foreach (var org in orgs)
        {
            var days = org.AuditRetentionDays ?? appConfig.AuditRetentionDays;
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            var deleted = await db.AuditLogs
                .Where(a => a.OrgId == org.Id && a.CreatedAt < cutoff)
                .ExecuteDeleteAsync(stoppingToken);
            total += deleted;
        }

        // Purge system-level logs (OrgId == null) using the global retention setting
        var systemCutoff = DateTimeOffset.UtcNow.AddDays(-appConfig.AuditRetentionDays);
        total += await db.AuditLogs
            .Where(a => a.OrgId == null && a.CreatedAt < systemCutoff)
            .ExecuteDeleteAsync(stoppingToken);

        if (total > 0 && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Audit log retention: purged {Count} expired entries", total);
    }
}
