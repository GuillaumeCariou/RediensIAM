using Microsoft.Extensions.Caching.Distributed;
using Npgsql;
using NpgsqlTypes;

namespace RediensIAM.Services;

/// <summary>
/// The deployment's shared, short-lived state, on PostgreSQL.
///
/// <para>
/// This replaces Dragonfly wholesale. It implements <see cref="IDistributedCache"/> so that
/// <c>AddSession</c> and every existing caller keep working unchanged, and it exposes the two
/// operations a cache interface cannot express — an atomic counter and the webhook queue — as
/// methods of its own.
/// </para>
///
/// <para>
/// <b>Raw Npgsql, not EF Core, and that is deliberate.</b> Three reasons, in order of weight:
/// </para>
/// <list type="number">
/// <item><see cref="Data.TenantScopeInterceptor"/> publishes <c>rediensiam.org_id</c> on every
/// <c>RediensIamDbContext</c> connection. This state is <b>deployment-wide</b> — a session cookie
/// and a rate-limit counter belong to no tenant — so running it through the application context
/// would put shared rows behind a tenant scope, which is at best confusing and at worst a
/// fail-closed outage once row-level security is on.</item>
/// <item>It is called on the hot path (every request with a session, every login attempt). Change
/// tracking, and a scope per call, buy nothing here.</item>
/// <item>The three statements below are the whole implementation. An ORM over four columns is the
/// abstraction this refactor exists to remove.</item>
/// </list>
///
/// <para>
/// <b>Expiry is a predicate, not a job.</b> Every read filters <c>expires_at</c>, so an expired
/// row is invisible the instant it expires — a TTL by another name. <see cref="ExpiredStateSweeper"/>
/// only reclaims the space, and correctness never waits for it.
/// </para>
/// </summary>
public sealed class PostgresSharedState(NpgsqlDataSource source) : IDistributedCache
{
    // ── IDistributedCache ─────────────────────────────────────────────────────

    public byte[]? Get(string key) => GetAsync(key).GetAwaiter().GetResult();

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var cmd = source.CreateCommand(
            "SELECT value FROM shared_state WHERE key = $1 AND (expires_at IS NULL OR expires_at > now())");
        cmd.Parameters.AddWithValue(key);
        return await cmd.ExecuteScalarAsync(token) as byte[];
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => SetAsync(key, value, options).GetAwaiter().GetResult();

    public async Task SetAsync(
        string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);

        await using var cmd = source.CreateCommand(
            """
            INSERT INTO shared_state (key, value, expires_at) VALUES ($1, $2, $3)
            ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, expires_at = EXCLUDED.expires_at
            """);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(value ?? []);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = (object?)AbsoluteExpiry(options) ?? DBNull.Value,
        });
        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Sliding expiration is deliberately not honoured, and nothing in this codebase asks for it:
    /// every caller sets <see cref="DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow"/>.
    /// Silently treating a sliding window as absolute would be a lie; refusing it would break
    /// <c>AddSession</c>, which sets one. Absolute-from-now is the honest reading, and it is what
    /// the session cookie's own <c>IdleTimeout</c> already means in practice.
    /// </summary>
    private static DateTimeOffset? AbsoluteExpiry(DistributedCacheEntryOptions o)
    {
        if (o.AbsoluteExpiration is { } at) return at;
        if (o.AbsoluteExpirationRelativeToNow is { } rel) return DateTimeOffset.UtcNow.Add(rel);
        if (o.SlidingExpiration is { } sliding) return DateTimeOffset.UtcNow.Add(sliding);
        return null;
    }

    public void Refresh(string key) { }

    /// <summary>No-op: see <see cref="AbsoluteExpiry"/>. There is no sliding window to extend.</summary>
    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => RemoveAsync(key).GetAwaiter().GetResult();

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var cmd = source.CreateCommand("DELETE FROM shared_state WHERE key = $1");
        cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync(token);
    }

    // ── Beyond IDistributedCache ──────────────────────────────────────────────

    /// <summary>
    /// True when the key exists and has not expired, without transferring the value.
    /// </summary>
    public async Task<bool> ExistsAsync(string key, CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand(
            "SELECT 1 FROM shared_state WHERE key = $1 AND (expires_at IS NULL OR expires_at > now())");
        cmd.Parameters.AddWithValue(key);
        return await cmd.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// Increments a fixed-window counter and returns its new value, in one statement.
    ///
    /// <para>
    /// The check and the increment must not be separable: a read-then-write pair lets concurrent
    /// attempts each observe the pre-increment count and share one slot of the budget. This is the
    /// property the Lua script existed to buy, and <c>INSERT … ON CONFLICT DO UPDATE … RETURNING</c>
    /// has it by definition — one statement, one row lock, no script cache.
    /// </para>
    ///
    /// <para>
    /// The window is fixed, not sliding: a row whose <c>window_end</c> has passed is reset to 1
    /// rather than incremented, which is exactly what <c>INCR</c> on an expired Redis key did.
    /// </para>
    /// </summary>
    public async Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand(
            """
            INSERT INTO rate_counters (key, count, window_end) VALUES ($1, 1, now() + $2)
            ON CONFLICT (key) DO UPDATE SET
                count      = CASE WHEN rate_counters.window_end > now() THEN rate_counters.count + 1 ELSE 1 END,
                window_end = CASE WHEN rate_counters.window_end > now() THEN rate_counters.window_end ELSE now() + $2 END
            RETURNING count
            """);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Interval, Value = window });
        return (long)(await cmd.ExecuteScalarAsync(token))!;
    }

    /// <summary>The counter's current value, or 0 when absent or past its window.</summary>
    public async Task<long> CounterAsync(string key, CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand(
            "SELECT count FROM rate_counters WHERE key = $1 AND window_end > now()");
        cmd.Parameters.AddWithValue(key);
        return await cmd.ExecuteScalarAsync(token) is long n ? n : 0L;
    }

    public async Task ResetCounterAsync(string key, CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand("DELETE FROM rate_counters WHERE key = $1");
        cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync(token);
    }

    // ── Webhook queue ─────────────────────────────────────────────────────────

    public async Task QueueWebhookAsync(string jobJson, long score, CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand(
            """
            INSERT INTO webhook_pending (job_json, score) VALUES ($1, $2)
            ON CONFLICT (job_json) DO UPDATE SET score = EXCLUDED.score
            """);
        cmd.Parameters.AddWithValue(jobJson);
        cmd.Parameters.AddWithValue(score);
        await cmd.ExecuteNonQueryAsync(token);
    }

    public async Task<string[]> PendingWebhooksAsync(CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand(
            "SELECT job_json FROM webhook_pending ORDER BY score");
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var jobs = new List<string>();
        while (await reader.ReadAsync(token)) jobs.Add(reader.GetString(0));
        return [.. jobs];
    }

    public async Task DequeueWebhookAsync(string jobJson, CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand("DELETE FROM webhook_pending WHERE job_json = $1");
        cmd.Parameters.AddWithValue(jobJson);
        await cmd.ExecuteNonQueryAsync(token);
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    /// <summary>Reclaims space taken by rows that are already invisible to every read.</summary>
    public async Task<int> SweepExpiredAsync(CancellationToken token = default)
    {
        await using var cmd = source.CreateCommand(
            """
            WITH s AS (DELETE FROM shared_state  WHERE expires_at IS NOT NULL AND expires_at <= now() RETURNING 1),
                 r AS (DELETE FROM rate_counters WHERE window_end <= now() - interval '1 hour'   RETURNING 1)
            SELECT (SELECT count(*) FROM s) + (SELECT count(*) FROM r)
            """);
        return (int)(long)(await cmd.ExecuteScalarAsync(token))!;
    }

    /// <summary>Round-trips the connection. Used by the readiness probe.</summary>
    public async Task<bool> PingAsync(CancellationToken token = default)
    {
        try
        {
            await using var cmd = source.CreateCommand("SELECT 1");
            return await cmd.ExecuteScalarAsync(token) is not null;
        }
        catch (NpgsqlException) { return false; }
    }
}

/// <summary>
/// Deletes rows that every read already ignores. Hourly, because nothing depends on it: an expired
/// row is invisible from the moment it expires, so this reclaims disk, not correctness.
/// </summary>
public sealed class ExpiredStateSweeper(PostgresSharedState state, ILogger<ExpiredStateSweeper> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var removed = await state.SweepExpiredAsync(stoppingToken);
                if (removed > 0 && logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Swept {Removed} expired shared-state rows", removed);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed sweep costs disk, never correctness — never kill the loop for it.
                logger.LogWarning(ex, "Expired shared-state sweep failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
