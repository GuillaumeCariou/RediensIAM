using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RediensIAM.Config;
using RediensIAM.Models;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// The application half of S-5 phase 2 (row-level security), step 18 items A-1, A-2 and A-4.
///
/// The policies in <c>deploy/rediensiam/files/rls.sql</c> are fail-closed: a connection that has
/// not set <c>rediensiam.org_id</c> sees zero rows in every tenant table. Enabling them against an
/// application that does not set it is a total outage, and enabling them against an application
/// that sets it on a connection which then keeps the value across a pool checkout is a
/// cross-tenant read. These tests are what makes it safe to turn the flag on.
/// </summary>
[Collection("RediensIAM")]
public class TenantScopeInterceptorTests(TestFixture fixture)
{
    private const string ReadScope = "SELECT current_setting('rediensiam.org_id', true)";

    // ── A-1: the scope reaches the database ───────────────────────────────────

    [Fact]
    public async Task Scope_Is_Set_On_The_Connection_The_Request_Actually_Uses()
    {
        var orgId = Guid.NewGuid();
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = ContextFor(orgId.ToString());
        try
        {
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

            // A real EF query first, so this is the connection the application would be using and
            // not one the test opened by hand.
            await db.Users.AsNoTracking().Take(1).ToListAsync();

            (await ReadScopeAsync(db)).Should().Be(orgId.ToString());
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Fact]
    public async Task Scope_Survives_Into_The_Transaction_EF_Opens_For_SaveChanges()
    {
        // SET LOCAL would be silently discarded outside a transaction; a plain SET is visible both
        // inside and outside one. This is the direction that would have been missed: a test that
        // only ever asserted inside a transaction would pass for SET LOCAL too.
        var orgId = Guid.NewGuid();
        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = ContextFor(orgId.ToString());
        try
        {
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RediensIamDbContext>();

            (await ReadScopeAsync(db)).Should().Be(orgId.ToString(), "outside any transaction");

            await using var tx = await db.Database.BeginTransactionAsync();
            (await ReadScopeAsync(db)).Should().Be(orgId.ToString(), "inside a transaction");
            await tx.RollbackAsync();
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    // ── A-2: the scope must not survive the pool ──────────────────────────────

    [Fact]
    public async Task Scope_Does_Not_Survive_Into_The_Next_Checkout_Of_The_Same_Connection()
    {
        // Pool of exactly one, so the second Open() is guaranteed to hand back the same physical
        // connection the first one dirtied. Without Npgsql's DISCARD ALL on return this reads back
        // the first tenant's uuid — which is the cross-tenant leak A-2 prohibits.
        var dsn = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString)
        {
            MaxPoolSize = 1,
            ApplicationName = "rls-pool-reset-probe",
        }.ConnectionString;

        var leaked = Guid.NewGuid().ToString();

        int backendPid;
        await using (var first = new NpgsqlConnection(dsn))
        {
            await first.OpenAsync();
            backendPid = first.ProcessID;
            await ExecuteAsync(first, $"SET rediensiam.org_id = '{leaked}'");
            (await ScalarAsync(first, ReadScope)).Should().Be(leaked);
        }

        await using (var second = new NpgsqlConnection(dsn))
        {
            await second.OpenAsync();
            second.ProcessID.Should().Be(backendPid, "the pool must have handed back the same physical connection for this to prove anything");
            var carried = await ScalarAsync(second, ReadScope);
            carried.Should().Match<string?>(v => string.IsNullOrEmpty(v),
                "a pooled connection must come back with no tenant scope, not with the previous renter's");
        }
    }

    [Fact]
    public void Dsn_That_Suppresses_The_Pool_Reset_Refuses_To_Start()
    {
        var act = () => ConfigWith("Host=localhost;Database=test;No Reset On Close=true").ConnectionString;
        act.Should().Throw<InvalidOperationException>().WithMessage("*No Reset On Close*");
    }

    [Fact]
    public void Dsn_That_Multiplexes_Refuses_To_Start()
    {
        // Multiplexing interleaves commands from different logical connections over one physical
        // session, so there is no per-request session left to scope.
        var act = () => ConfigWith("Host=localhost;Database=test;Multiplexing=true").ConnectionString;
        act.Should().Throw<InvalidOperationException>().WithMessage("*Multiplexing*");
    }

    [Fact]
    public void Ordinary_Dsn_Is_Accepted_Unchanged()
    {
        const string dsn = "Host=localhost;Database=test;Username=u;Password=p";
        ConfigWith(dsn).ConnectionString.Should().Be(dsn);
    }

    // ── A-1: 'system' where it belongs, and only where it belongs ─────────────

    [Theory]
    // No HTTP context at all: migrations, the bootstrap super admin, the audit retention sweep,
    // the webhook dispatcher — deployment-wide work that is not any one tenant's.
    [InlineData(null, TenantScopeInterceptor.SystemScope)]
    // Authenticated but org-less: SuperAdmin listings across organisations.
    [InlineData("", TenantScopeInterceptor.SystemScope)]
    // Guid.Empty is the null/empty conflation ServiceAccountController.cs:29-33 documents. It must
    // never be treated as a real organisation id.
    [InlineData("00000000-0000-0000-0000-000000000000", TenantScopeInterceptor.SystemScope)]
    [InlineData("not-a-uuid", TenantScopeInterceptor.SystemScope)]
    public void Unscoped_Callers_Run_As_System(string? orgId, string expected)
    {
        var context = orgId is null ? null : ContextFor(orgId);
        new TenantScopeInterceptor(new StubAccessor { HttpContext = context })
            .CurrentScope().Should().Be(expected);
    }

    [Fact]
    public void Unauthenticated_Request_Runs_As_System()
    {
        // The login path: a request is in flight but GatewayAuthMiddleware has put no claims on it,
        // because the user is being looked up by e-mail before any tenant is known.
        new TenantScopeInterceptor(new StubAccessor { HttpContext = new DefaultHttpContext() })
            .CurrentScope().Should().Be(TenantScopeInterceptor.SystemScope);
    }

    [Fact]
    public void Tenant_Caller_Runs_Scoped_To_Its_Own_Organisation()
    {
        var orgId = Guid.NewGuid();
        new TenantScopeInterceptor(new StubAccessor { HttpContext = ContextFor(orgId.ToString()) })
            .CurrentScope().Should().Be(orgId.ToString());
    }

    [Fact]
    public async Task System_Scope_Reaches_The_Database_As_The_Literal_The_Policies_Expect()
    {
        // 'system' is not a spelling the application may get wrong: rls.sql compares against this
        // exact string and anything else is fail-closed, which is an outage rather than a leak.
        var interceptor = new TenantScopeInterceptor(new StubAccessor());
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();

        await interceptor.ConnectionOpenedAsync(connection, null!);

        (await ScalarAsync(connection, ReadScope)).Should().Be("system");
    }

    [Fact]
    public async Task Interceptor_Overwrites_A_Previous_Renters_Scope_Rather_Than_Inheriting_It()
    {
        // Belt to A-2's braces: even on a connection that somehow arrived dirty, the interceptor
        // writes the current request's scope over it. Both layers have to fail to leak.
        var interceptor = new TenantScopeInterceptor(new StubAccessor());
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"SET rediensiam.org_id = '{Guid.NewGuid()}'");

        await interceptor.ConnectionOpenedAsync(connection, null!);

        (await ScalarAsync(connection, ReadScope)).Should().Be("system");
    }

    [Fact]
    public async Task Scope_Value_Is_Bound_As_A_Parameter_Not_Concatenated()
    {
        // The scope can only ever be "system" or a formatted Guid, so this is belt-and-braces —
        // but it is the belt that keeps a future refactor from turning the claim into a SQL sink.
        var hostile = "system'; DROP TABLE users; --";
        var interceptor = new TenantScopeInterceptor(new StubAccessor { HttpContext = ContextFor(hostile) });

        interceptor.CurrentScope().Should().Be(TenantScopeInterceptor.SystemScope);

        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await interceptor.ConnectionOpenedAsync(connection, null!);
        (await ScalarAsync(connection, "SELECT to_regclass('public.users') IS NOT NULL"))
            .Should().Be("True");
    }

    // ── A-4: the two lists name the same paths ────────────────────────────────

    [Fact]
    public void Model_Declares_No_Global_Query_Filter_So_The_IgnoreQueryFilters_Exemption_Set_Is_Empty()
    {
        // A-4 asks that the IgnoreQueryFilters() call sites and the 'system' scope list name the
        // same paths. IgnoreQueryFilters() can only bypass a filter HasQueryFilter declared, and
        // the model declares none — so the exemption set is empty and the two agree trivially.
        //
        // This test is what stops that from silently stopping being true: adding a query filter
        // fails here, and the fix is to audit every IgnoreQueryFilters() against
        // TenantScopeInterceptor.LegitimatelyUnscopedPaths before this assertion is relaxed.
        var filtered = fixture.Db.Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count > 0)
            .Select(e => e.ClrType.Name)
            .ToList();

        filtered.Should().BeEmpty(
            "a global query filter was added; every IgnoreQueryFilters() that bypasses it must " +
            "correspond to an entry in TenantScopeInterceptor.LegitimatelyUnscopedPaths, or a " +
            "query bypasses one isolation layer and not the other");
    }

    [Fact]
    public void The_Unscoped_Path_List_Is_A_Real_Artefact()
    {
        // The honest limit, kept greppable rather than remembered. If this shrinks to nothing,
        // someone has decided RLS covers the login path — it does not.
        TenantScopeInterceptor.LegitimatelyUnscopedPaths.Should().NotBeEmpty();
        TenantScopeInterceptor.LegitimatelyUnscopedPaths.Should().Contain(p => p.Contains("AuthController"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static AppConfig ConfigWith(string dsn) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = dsn })
        .Build());

    private static DefaultHttpContext ContextFor(string orgId)
    {
        var context = new DefaultHttpContext();
        context.Items["Claims"] = new TokenClaims
        {
            UserId = Guid.NewGuid().ToString(),
            OrgId = orgId,
            ProjectId = "",
            Roles = [],
        };
        return context;
    }

    private static async Task<string?> ReadScopeAsync(RediensIamDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            return await ScalarAsync(db.Database.GetDbConnection(), ReadScope);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : value.ToString();
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StubAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
