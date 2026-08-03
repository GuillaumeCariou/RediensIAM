using RediensIAM.Controllers;

namespace RediensIAM.Services;

/// <summary>
/// Server-side validation of a tenant-supplied SMTP endpoint.
///
/// Webhooks, OIDC issuers and SAML metadata URLs all pass through
/// <see cref="WebhookUrlValidator"/>; the per-org SMTP host did not, which made
/// <c>POST /org/smtp/test</c> a synchronous connect primitive against any host and port the pod
/// can reach. Both write paths (<c>PUT /org/smtp</c> and <c>PUT /admin/organizations/{id}/smtp</c>)
/// go through this, which is why it lives here rather than in either controller.
/// </summary>
public static class SmtpEndpointValidator
{
    /// <summary>
    /// Submission and relay ports only. Without this the endpoint is a port scanner: any port a
    /// connect attempt can distinguish is one bit of the pod's reachable network.
    /// 1025 is MailHog/Mailpit, which development deployments actually use.
    /// </summary>
    private static readonly int[] AllowedPorts = [25, 465, 587, 1025, 2525];

    /// <summary>Implicit TLS ("SMTPS"). StartTls is meaningless on this port — the socket is already TLS.</summary>
    public const int ImplicitTlsPort = 465;

    /// <summary>Returns an error code, or null when the endpoint is acceptable.</summary>
    public static async Task<string?> ValidateAsync(string? host, int port, bool startTls)
    {
        if (string.IsNullOrWhiteSpace(host)) return "smtp_host_required";
        if (host.Length > 255)               return "smtp_host_too_long";
        if (!AllowedPorts.Contains(port))    return "smtp_port_not_allowed";

        // Credentials and message bodies travel over this socket. Cleartext submission is only
        // ever a local-relay arrangement, and a per-org host is by definition not that.
        if (!startTls && port != ImplicitTlsPort) return "smtp_tls_required";

        // Same resolver and same reserved-range set as every other outbound target.
        if (await WebhookUrlValidator.IsPrivateOrReservedHostAsync(host))
            return "smtp_host_not_allowed";

        return null;
    }
}
