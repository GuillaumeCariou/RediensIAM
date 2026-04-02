using Prometheus;

namespace RediensIAM.Services;

/// <summary>
/// Shared Prometheus metric descriptors for RediensIAM.
/// Declare as static so all code paths share the same counter/gauge instances.
/// </summary>
public static class IamMetrics
{
    public static readonly Counter LoginAttempts = Metrics.CreateCounter(
        "iam_login_attempts_total",
        "Total login attempts",
        new CounterConfiguration { LabelNames = ["result"] });
    // result: success | failure | locked | mfa_required | mfa_setup_required

    public static readonly Counter RegistrationAttempts = Metrics.CreateCounter(
        "iam_registration_attempts_total",
        "Total self-registration attempts",
        new CounterConfiguration { LabelNames = ["result"] });
    // result: success | rate_limited | domain_not_allowed | email_exists | verification_pending

    public static readonly Counter AuditEvents = Metrics.CreateCounter(
        "iam_audit_events_total",
        "Total audit events recorded",
        new CounterConfiguration { LabelNames = ["action"] });

    public static readonly Counter WebhookDispatch = Metrics.CreateCounter(
        "iam_webhook_dispatch_total",
        "Webhook delivery attempts",
        new CounterConfiguration { LabelNames = ["result"] });
    // result: delivered | failed

    public static readonly Gauge ActiveWebhooks = Metrics.CreateGauge(
        "iam_active_webhooks",
        "Number of active registered webhooks");
}
