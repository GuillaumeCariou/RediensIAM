using Prometheus;

namespace RediensIAM.Services;

/// <summary>
/// Shared Prometheus metric descriptors for RediensIAM.
/// Declare as static so all code paths share the same counter/gauge instances.
/// </summary>
public static class IamMetrics
{
    /// <summary>
    /// <c>result</c> is one of: success, failure, locked, mfa_required, mfa_setup_required.
    /// Dashboards and alert rules match on these strings, so adding a value is a contract change.
    /// </summary>
    public static readonly Counter LoginAttempts = Metrics.CreateCounter(
        "iam_login_attempts_total",
        "Total login attempts",
        new CounterConfiguration { LabelNames = ["result"] });

    /// <summary>
    /// <c>result</c> is one of: success, rate_limited, domain_not_allowed, email_exists,
    /// verification_pending. Same contract caveat as <see cref="LoginAttempts"/>.
    /// </summary>
    public static readonly Counter RegistrationAttempts = Metrics.CreateCounter(
        "iam_registration_attempts_total",
        "Total self-registration attempts",
        new CounterConfiguration { LabelNames = ["result"] });

    public static readonly Counter AuditEvents = Metrics.CreateCounter(
        "iam_audit_events_total",
        "Total audit events recorded",
        new CounterConfiguration { LabelNames = ["action"] });

    /// <summary>
    /// <c>result</c> is one of: delivered, failed. Same contract caveat as
    /// <see cref="LoginAttempts"/>.
    /// </summary>
    public static readonly Counter WebhookDispatch = Metrics.CreateCounter(
        "iam_webhook_dispatch_total",
        "Webhook delivery attempts",
        new CounterConfiguration { LabelNames = ["result"] });

    public static readonly Gauge ActiveWebhooks = Metrics.CreateGauge(
        "iam_active_webhooks",
        "Number of active registered webhooks");

    /// <summary>
    /// Organisations whose audit hash chain has a broken link, as of the last integrity pass.
    /// <b>Anything above zero is a rewritten or removed audit row.</b> Alert on it — see
    /// <c>IntegrityMonitorService</c>.
    /// </summary>
    public static readonly Gauge AuditChainBroken = Metrics.CreateGauge(
        "iam_audit_chain_broken_orgs",
        "Organisations whose audit hash chain does not verify");

    /// <summary>
    /// Audit rows the deployment cannot vouch for: written before the chain existed, before it was
    /// keyed, or under a retired key. Expected to be a fixed historic number that only ever falls
    /// as retention purges it away — a <i>rise</i> means rows are being written outside the
    /// application, or keyed rows are being downgraded.
    /// </summary>
    public static readonly Gauge AuditChainUnverifiableRows = Metrics.CreateGauge(
        "iam_audit_chain_unverifiable_rows",
        "Audit rows that predate chain keying or are under a key no longer configured");

    /// <summary>
    /// Grants the two stores disagree about. <c>class</c> is <c>orphan_tuple</c> (in Keto, no
    /// backing row — live privilege with no provenance) or <c>orphan_row</c> (row, no tuple —
    /// dead as authorisation, still a scope source for the consent path).
    /// </summary>
    public static readonly Gauge GrantDivergence = Metrics.CreateGauge(
        "iam_grant_divergence",
        "Grants present in one of Keto/Postgres and not the other",
        new GaugeConfiguration { LabelNames = ["class"] });

    /// <summary>
    /// Database connection checkouts by the RLS scope they were opened under. <c>scope</c> is
    /// <c>org</c> (a real tenant) or <c>system</c> (unscoped — RLS enforces nothing on it).
    /// The ratio is the measurable form of the honest limit documented in
    /// <c>TenantScopeInterceptor.LegitimatelyUnscopedPaths</c>: it should fall, never rise.
    /// </summary>
    public static readonly Counter DbConnectionScope = Metrics.CreateCounter(
        "iam_db_connection_scope_total",
        "Database connections opened, by RLS tenant scope",
        new CounterConfiguration { LabelNames = ["scope"] });
}
