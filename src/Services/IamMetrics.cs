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
}
