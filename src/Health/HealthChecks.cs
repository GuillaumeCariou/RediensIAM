using Microsoft.Extensions.Diagnostics.HealthChecks;
using RediensIAM.Data;

namespace RediensIAM.Health;

// Out of Program.cs, where they had no namespace at all: a top-level-statements file cannot open
// one part-way through, so every type declared after the statements lands in the global namespace.

/// <summary>Answers unhealthy when the application database cannot be reached.</summary>
public sealed class DatabaseHealthCheck(RediensIamDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("database unreachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("database unreachable", ex);
        }
    }
}
