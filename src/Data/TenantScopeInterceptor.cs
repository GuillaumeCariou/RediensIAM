using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RediensIAM.Middleware;
using RediensIAM.Services;

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
/// <b>Two sources of scope.</b> For authenticated traffic it is the caller's validated token
/// claims. For the login flow there is no token yet, so <see cref="PinToOrganisationAsync"/>
/// publishes the organisation the flow has already been bound to — the Hydra login challenge
/// names an OAuth2 client, the client's registered <c>metadata.project_id</c> names a project,
/// and the project names an organisation. Everything after that point runs scoped.
/// </para>
///
/// <para>
/// <b>The honest limit.</b> Resolving that organisation costs one read of <c>projects</c>, and
/// that read cannot itself be scoped — it is what determines the scope. Some flows have no
/// organisation at all: the admin console signs users in from the <c>__system__</c> user list,
/// whose <c>OrgId IS NULL</c> makes it invisible under every tenant scope by construction, and
/// the token-keyed paths (e-mail verification, invite completion, password-reset confirmation)
/// identify their subject by a random token rather than by a tenant. Those run as
/// <c>'system'</c> and are listed in <see cref="LegitimatelyUnscopedPaths"/>.
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
        // Pre-authentication paths that CANNOT know a tenant. The rest of the login flow no
        // longer belongs here: it is pinned by PinToOrganisationAsync once the challenge has
        // been bound to a project.
        "AuthController.AdminLogin — the __system__ user list has OrgId IS NULL, so it is invisible under every tenant scope by design",
        "AuthController — the fallback read of `projects` for a client registered before org_id was in its metadata; that read is what decides the scope, so it cannot run under it",
        "AuthController.VerifyEmail / CompleteInvite / VerifyPasswordReset / ConfirmPasswordReset — the subject is named by a random token, not by a tenant",
        "GatewayAuthMiddleware — PAT introspection, which finds the token's owner by hash before the org is known",
        "SamlController.AssertionConsumerService — the read of `saml_idp_configs` that names the IdP's project; the ACS's challenge arrives in browser-controlled RelayState and is not a scope source, so that row is what decides the scope and cannot run under it. Everything after it is pinned (see 38-residual-code-fixes.md)",

        // Deployment-wide work that is not any one tenant's.
        "Program.EnsureDbSchemaAsync — EF migrations",
        "Program.BootstrapSuperAdminAsync — the __system__ user list and the bootstrap super admin",
        "InstanceConfigurationProvider — the instances table (deployment-global, no RLS policy)",
        "AuditLogRetentionService — sweeps every organisation's expired rows, plus the OrgId IS NULL rows",
        "IntegrityMonitorService — verifies every organisation's audit chain and compares every grant against Keto; a per-tenant scope would hide exactly the rows it is looking for",
        "WebhookDispatcherService — drains one queue for all tenants",

        // Cross-tenant by design, and gated by authorisation rather than by scope.
        "SystemAdminController — SuperAdmin listings across organisations",
        "AuthController.GetConsent, admin-client branch — finds which organisations a console user administers, which is the question itself",
    ];

    /// <summary>Where <see cref="PinToOrganisationAsync"/> parks the scope for the rest of the request.</summary>
    private const string PinnedScopeKey = "RediensIAM.PinnedOrgScope";

    /// <summary>
    /// Binds the remainder of this request to <paramref name="orgId"/>, for flows that establish
    /// their tenant before any token exists — the login challenge names an OAuth2 client, the
    /// client names a project, the project names this organisation.
    ///
    /// <para>
    /// <paramref name="orgId"/> must come from a row the server read, never from request input.
    /// The caller-supplied <c>project_id</c> on a login challenge is cross-check material only
    /// (see <see cref="Services.LoginChallengeProject"/>); pinning to it would let one tenant
    /// hand itself another tenant's scope.
    /// </para>
    ///
    /// <para>
    /// Refuses to run for a caller whose token already names a <i>different</i> organisation:
    /// that request is scoped by its own credential, and moving that scope would be a privilege
    /// change wearing a performance optimisation's clothes. A token naming no organisation, or
    /// the same one, is no conflict — and the check is deliberately narrow so that a stray
    /// Authorization header on an ordinary login cannot turn into a 500.
    /// </para>
    ///
    /// Also issues the setting on the connection the context is holding right now, so the pin
    /// applies whether or not the next query opens a fresh one.
    /// </summary>
    public async Task PinToOrganisationAsync(DbContext db, Guid orgId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orgId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(orgId), "Guid.Empty is not an organisation.");

        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("PinToOrganisationAsync needs a request in flight.");
        if (http.GetClaims() is { } claims
            && Guid.TryParse(claims.OrgId, out var tokenOrgId)
            && tokenOrgId != Guid.Empty
            && tokenOrgId != orgId)
            throw new InvalidOperationException(
                "Refusing to re-scope a request already scoped to another organisation by its token.");

        http.Items[PinnedScopeKey] = orgId.ToString();

        // A closed connection needs nothing: the next checkout runs ConnectionOpenedAsync, which
        // now reads the pin. Only a connection already open (an explicit transaction, a
        // still-streaming reader) has to be told, and issuing it unconditionally cost a round
        // trip on every pin — measurably, +3 checkouts per login.
        if (db.Database.GetDbConnection().State == ConnectionState.Open)
            await db.Database
                .ExecuteSqlRawAsync("SELECT set_config({0}, {1}, false)", [SettingName, orgId.ToString()], cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// The scope this request runs under: the organisation
    /// <see cref="PinToOrganisationAsync"/> pinned, else the claims
    /// <c>GatewayAuthMiddleware</c> put on the context. Absent, unparseable or
    /// <see cref="Guid.Empty"/> means unscoped.
    /// </summary>
    public string CurrentScope()
    {
        var http = httpContextAccessor.HttpContext;
        if (http?.Items.TryGetValue(PinnedScopeKey, out var pinned) == true && pinned is string orgScope)
            return orgScope;

        var claims = http?.GetClaims();
        return claims is not null
            && Guid.TryParse(claims.OrgId, out var orgId)
            && orgId != Guid.Empty
                ? orgId.ToString()
                : SystemScope;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = ScopeCommand(connection, Observed(CurrentScope()));
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var command = ScopeCommand(connection, Observed(CurrentScope()));
        await using (command.ConfigureAwait(false))
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts the checkout so the scoped/unscoped ratio is measurable rather than asserted.</summary>
    private static string Observed(string scope)
    {
        IamMetrics.DbConnectionScope.WithLabels(scope == SystemScope ? SystemScope : "org").Inc();
        return scope;
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
