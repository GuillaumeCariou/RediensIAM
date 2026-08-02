using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RediensIAM.Data;
using RediensIAM.Health;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;

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

    [Theory]
    [InlineData(true, HealthStatus.Healthy)]
    [InlineData(false, HealthStatus.Unhealthy)]
    public async Task The_cache_probe_follows_the_multiplexer(bool connected, HealthStatus expected)
    {
        var result = await new CacheHealthCheck(new StubMultiplexer(connected)).CheckHealthAsync(Context);

        Assert.Equal(expected, result.Status);
        if (expected == HealthStatus.Unhealthy) Assert.Equal("cache disconnected", result.Description);
    }

    /// <summary>Only <see cref="IConnectionMultiplexer.IsConnected"/> is read; the rest is unused.</summary>
    private sealed class StubMultiplexer(bool connected) : IConnectionMultiplexer
    {
        public bool IsConnected => connected;

        public string ClientName => throw new NotSupportedException();
        public string Configuration => throw new NotSupportedException();
        public int TimeoutMilliseconds => throw new NotSupportedException();
        public long OperationCount => throw new NotSupportedException();
        public bool PreserveAsyncOrder { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public bool IsConnecting => throw new NotSupportedException();
        public bool IncludeDetailInExceptions { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int StormLogThreshold { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public event EventHandler<RedisErrorEventArgs>? ErrorMessage { add { } remove { } }
        public event EventHandler<ConnectionFailedEventArgs>? ConnectionFailed { add { } remove { } }
        public event EventHandler<InternalErrorEventArgs>? InternalError { add { } remove { } }
        public event EventHandler<ConnectionFailedEventArgs>? ConnectionRestored { add { } remove { } }
        public event EventHandler<EndPointEventArgs>? ConfigurationChanged { add { } remove { } }
        public event EventHandler<EndPointEventArgs>? ConfigurationChangedBroadcast { add { } remove { } }
        public event EventHandler<HashSlotMovedEventArgs>? HashSlotMoved { add { } remove { } }
        public event EventHandler<ServerMaintenanceEvent>? ServerMaintenanceEvent { add { } remove { } }

        public void AddLibraryNameSuffix(string suffix) => throw new NotSupportedException();
        public void Close(bool allowCommandsToComplete = true) => throw new NotSupportedException();
        public Task CloseAsync(bool allowCommandsToComplete = true) => throw new NotSupportedException();
        public bool Configure(TextWriter? log = null) => throw new NotSupportedException();
        public Task<bool> ConfigureAsync(TextWriter? log = null) => throw new NotSupportedException();
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void ExportConfiguration(Stream destination, ExportOptions options = (ExportOptions)(-1)) => throw new NotSupportedException();
        public ServerCounters GetCounters() => throw new NotSupportedException();
        public IDatabase GetDatabase(int db = -1, object? asyncState = null) => throw new NotSupportedException();
        public global::System.Net.EndPoint[] GetEndPoints(bool configuredOnly = false) => throw new NotSupportedException();
        public int GetHashSlot(RedisKey key) => throw new NotSupportedException();
        public IServer GetServer(string host, int port, object? asyncState = null) => throw new NotSupportedException();
        public IServer GetServer(string hostAndPort, object? asyncState = null) => throw new NotSupportedException();
        public IServer GetServer(global::System.Net.IPAddress host, int port) => throw new NotSupportedException();
        public IServer GetServer(global::System.Net.EndPoint endpoint, object? asyncState = null) => throw new NotSupportedException();
        public IServer GetServer(RedisKey key, object? asyncState = null, CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
        public IServer[] GetServers() => throw new NotSupportedException();
        public string GetStatus() => throw new NotSupportedException();
        // IConnectionMultiplexer redeclares ToString() as non-nullable; matching it keeps the
        // compiler from warning about the narrowing.
        public override string ToString() => nameof(StubMultiplexer);
        public void GetStatus(TextWriter log) => throw new NotSupportedException();
        public string? GetStormLog() => throw new NotSupportedException();
        public ISubscriber GetSubscriber(object? asyncState = null) => throw new NotSupportedException();
        public int HashSlot(RedisKey key) => throw new NotSupportedException();
        public long PublishReconfigure(CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
        public Task<long> PublishReconfigureAsync(CommandFlags flags = CommandFlags.None) => throw new NotSupportedException();
        public void RegisterProfiler(Func<StackExchange.Redis.Profiling.ProfilingSession?> profilingSessionProvider) => throw new NotSupportedException();
        public void ResetStormLog() => throw new NotSupportedException();
        public void Wait(Task task) => throw new NotSupportedException();
        public T Wait<T>(Task<T> task) => throw new NotSupportedException();
        public void WaitAll(params Task[] tasks) => throw new NotSupportedException();
    }
}
