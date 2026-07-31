using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RediensIAM.Middleware;

namespace RediensIAM.Data;

/// <summary>
/// Publishes the request's tenant scope to PostgreSQL as <c>rediensiam.org_id</c> on every
/// connection open, which is the session contract the row-level security policies in
/// <c>deploy/rediensiam/files/rls.sql</c> read (step 18 item A-1).
///
/// <code>
/// rediensiam.org_id = '&lt;org uuid&gt;'  → that organisation only
/// rediensiam.org_id = 'system'        → unscoped
/// unset / empty / malformed           → nothing is visible, in every tenant table
/// </code>
///
/// <para>
/// <b>Session <c>SET</c>, not <c>SET LOCAL</c>.</b> Most EF Core reads run outside an explicit
/// transaction, where <c>SET LOCAL</c> does not error — it simply has no effect. That would leave
/// RLS returning zero rows in production while appearing to work in any test that happened to open
/// a transaction. <c>set_config(name, value, is_local =&gt; false)</c> is session scope, and takes
/// the value as a parameter rather than by string concatenation.
/// </para>
///
/// <para>
/// <b>On connection open, not on <c>DbContext</c> construction.</b> Npgsql calls
/// <c>Open()</c> on every rent from the pool and issues <c>DISCARD ALL</c> on return, so the
/// setting is written exactly once per checkout and cleared before the next one. That reset is
/// load-bearing and is guarded in <c>AppConfig.ConnectionString</c> (item A-2): a DSN carrying
/// <c>No Reset On Close=true</c> or <c>Multiplexing=true</c> refuses to start.
/// </para>
///
/// <para>
/// <b>The honest limit.</b> The scope is read from the caller's validated token claims, so any
/// request that has no token, or whose token names no organisation, runs as <c>'system'</c> —
/// unscoped. That is a substantial share of traffic and it is not a defect of this class; login
/// looks a user up by e-mail before any tenant is known. The full list is in
/// <see cref="LegitimatelyUnscopedPaths"/>. RLS protects tenant-scoped API traffic; it does not
/// make the login path tenant-safe.
/// </para>
/// </summary>
public sealed class TenantScopeInterceptor(IHttpContextAccessor httpContextAccessor) : DbConnectionInterceptor
{
    /// <summary>The PostgreSQL session setting the RLS policies read.</summary>
    public const string SettingName = "rediensiam.org_id";

    /// <summary>The unscoped sentinel. Anything else must parse as a non-empty organisation UUID.</summary>
    public const string SystemScope = "system";

    /// <summary>
    /// Every path that legitimately runs unscoped, as a greppable artefact rather than as
    /// knowledge somebody has. This is the same list that item A-4 requires the
    /// <c>IgnoreQueryFilters()</c> call sites to agree with; today that set is empty (the model
    /// declares no global query filter — pinned by <c>RlsScopeAlignmentTests</c>), so the two
    /// agree trivially. Adding a query filter without extending this list, or vice versa, means a
    /// query bypasses one layer and not the other.
    /// </summary>
    public static readonly string[] LegitimatelyUnscopedPaths =
    [
        // Anything before authentication: no token has been presented, so no organisation is
        // known. This is the bulk of it and it is why S-5 cannot be reported as closed.
        "AuthController — login, password reset, e-mail verification, social/SAML callbacks (user looked up by e-mail)",
        "GatewayAuthMiddleware — PAT introspection, which finds the token's owner by hash before the org is known",

        // Deployment-wide work that is not any one tenant's.
        "Program.EnsureDbSchemaAsync — EF migrations",
        "Program.BootstrapSuperAdminAsync — the __system__ user list and the bootstrap super admin",
        "InstanceConfigurationProvider — the instances table (deployment-global, no RLS policy)",
        "AuditLogRetentionService — sweeps every organisation's expired rows, plus the OrgId IS NULL rows",
        "WebhookDispatcherService — drains one queue for all tenants",

        // Cross-tenant by design, and gated by authorisation rather than by scope.
        "SystemAdminController — SuperAdmin listings across organisations",
    ];

    /// <summary>
    /// The scope this request runs under. Reads the claims
    /// <c>GatewayAuthMiddleware</c> put on the context; absent, unparseable or
    /// <see cref="Guid.Empty"/> means unscoped.
    /// </summary>
    public string CurrentScope()
    {
        var claims = httpContextAccessor.HttpContext?.GetClaims();
        return claims is not null
            && Guid.TryParse(claims.OrgId, out var orgId)
            && orgId != Guid.Empty
                ? orgId.ToString()
                : SystemScope;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = ScopeCommand(connection, CurrentScope());
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var command = ScopeCommand(connection, CurrentScope());
        await using (command.ConfigureAwait(false))
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DbCommand ScopeCommand(DbConnection connection, string scope)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config(@name, @value, false)";
        command.Parameters.Add(Parameter(command, "name", SettingName));
        command.Parameters.Add(Parameter(command, "value", scope));
        return command;
    }

    private static DbParameter Parameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        return parameter;
    }
}
