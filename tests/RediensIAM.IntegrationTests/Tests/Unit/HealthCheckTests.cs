using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RediensIAM.Data;
using RediensIAM.Health;

namespace RediensIAM.IntegrationTests.Tests.Unit;

/// <summary>
/// The two liveness probes. Their whole job is to answer unhealthy rather than throw: a probe that
/// throws is a 500 the orchestrator reads as "unhealthy" anyway, but with a stack trace on an
/// unauthenticated endpoint and nothing said about which dependency failed.
///
/// No fixture: a context pointed at an unreachable host and a stub multiplexer are enough, and
/// neither needs a container.
/// </summary>
[Collection("RediensIAM")]
public class HealthCheckTests(TestFixture fixture)
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("probe", _ => null!, null, null),
    };

    /// <summary>A context whose connection string names a host that will not answer.</summary>
    private static RediensIamDbContext UnreachableDb() =>
        new(new DbContextOptionsBuilder<RediensIamDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Timeout=1;Command Timeout=1")
            .Options);

    [Fact]
    public async Task An_unreachable_database_is_reported_unhealthy_rather_than_thrown()
    {
        using var db = UnreachableDb();

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("database unreachable", result.Description);
    }

    [Fact]
    public async Task A_reachable_database_is_healthy()
    {
        var result = await new DatabaseHealthCheck(fixture.GetService<RediensIamDbContext>())
            .CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

}
