using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using RediensIAM.Config;
using RediensIAM.Data;

namespace RediensIAM.Services;

// ── Email ──────────────────────────────────────────────────────────────────

public interface IEmailService
{
    Task SendOtpAsync(string to, string code, string purpose, Guid? orgId = null, Guid? projectId = null);
    /// <summary>
    /// Sends an invite. <paramref name="orgId"/> selects the organisation's own SMTP relay; it used
    /// to be a projectId that no caller ever passed, so an org that had configured its own relay
    /// still had every invite go out through the global one — or, on a deployment with no global
    /// relay, not go out at all, leaving an inactive user nobody could recover.
    /// </summary>
    Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? orgId = null);
    Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt, Guid? orgId = null);
    /// <summary>Connect, optionally authenticate, then disconnect. Throws on failure.</summary>
    Task CheckConnectivityAsync();
}

public class StubEmailService(ILogger<StubEmailService> logger) : IEmailService
{
    public Task SendOtpAsync(string to, string code, string purpose, Guid? orgId = null, Guid? projectId = null)
    {
        logger.LogWarning("[STUB EMAIL] To={To} Purpose={Purpose}", to, purpose);
        return Task.CompletedTask;
    }

    public Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? orgId = null)
    {
        logger.LogWarning("[STUB EMAIL] Invite To={To} Org={Org} Url={Url}", to, orgName, inviteUrl);
        return Task.CompletedTask;
    }

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt, Guid? orgId = null)
    {
        logger.LogWarning("[STUB EMAIL] NewDevice To={To} Ip={Ip}", to, ipAddress);
        return Task.CompletedTask;
    }

    public Task CheckConnectivityAsync() => Task.CompletedTask;
}

public class SmtpEmailService(
    AppConfig appConfig,
    RediensIamDbContext db,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    public async Task SendOtpAsync(string to, string code, string purpose, Guid? orgId = null, Guid? projectId = null)
    {
        // Expiry quoted in the mail must match Security:OtpTtlSeconds (default 300s), not a
        // hardcoded "10 minutes".
        var expiryMinutes = Math.Max(1, appConfig.OtpTtlSeconds / 60);

        // ── Resolve SMTP config ──────────────────────────────────────────────
        string? host;
        int port;
        bool startTls;
        string? username;
        string? password;
        string fromAddress;
        string fromName;

        var orgConfig = orgId.HasValue
            ? await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == orgId.Value)
            : null;

        // A tenant-supplied relay is re-validated every time it is used, not only when it was
        // saved: DNS is the tenant's to change afterwards, and this is the one outbound path that
        // does not dial through the SSRF-safe connect callback. The operator's own global relay is
        // deliberately exempt — an in-cluster smarthost on a private address is the normal shape,
        // and it comes from configuration rather than from a tenant.
        if (orgConfig != null
            && await SmtpEndpointValidator.ValidateAsync(orgConfig.Host, orgConfig.Port, orgConfig.StartTls) is { } orgSmtpError)
        {
            throw new InvalidOperationException($"org smtp endpoint refused: {orgSmtpError}");
        }

        if (orgConfig != null)
        {
            host        = orgConfig.Host;
            port        = orgConfig.Port;
            startTls    = orgConfig.StartTls;
            username    = orgConfig.Username;
            password    = orgConfig.PasswordEnc != null
                ? Encoding.UTF8.GetString(TotpEncryption.Decrypt(
                    appConfig.SmtpEncKey, orgConfig.PasswordEnc))
                : null;
            fromAddress = orgConfig.FromAddress;
            fromName    = orgConfig.FromName;
        }
        else if (!string.IsNullOrEmpty(appConfig.SmtpHost))
        {
            host        = appConfig.SmtpHost;
            port        = appConfig.SmtpPort;
            startTls    = appConfig.SmtpStartTls;
            username    = appConfig.SmtpUsername;
            password    = appConfig.SmtpPassword;
            fromAddress = appConfig.SmtpFromAddress;
            fromName    = appConfig.SmtpFromName;
        }
        else
        {
            logger.LogWarning("[EMAIL NO-OP] No SMTP configured. To={To} Purpose={Purpose}", to, purpose);
            return;
        }

        // ── Project-level from-name override ────────────────────────────────
        if (projectId.HasValue)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId.Value);
            if (!string.IsNullOrEmpty(project?.EmailFromName))
                fromName = project.EmailFromName;
        }

        // ── Build message ────────────────────────────────────────────────────
        var subject = purpose switch
        {
            "registration"   => "Your verification code",
            "password_reset" => "Your password reset code",
            _                => "Your verification code"
        };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = subject;
        message.Body = new TextPart("plain")
        {
            Text = $"Your {subject.ToLower()} is: {code}\n\nThis code expires in {expiryMinutes} minute(s)."
        };

        await SmtpSendAsync(host, port, startTls, username, password, message);
    }

    public async Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? orgId = null)
    {
        // ── Resolve SMTP config ──────────────────────────────────────────────
        string? host;
        int port;
        bool startTls;
        string? username;
        string? password;
        string fromAddress;
        string fromName;

        var orgConfig = orgId is Guid tenantOrgId
            ? await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == tenantOrgId)
            : null;

        // A tenant-supplied relay is re-validated every time it is used, not only when it was
        // saved: DNS is the tenant's to change afterwards, and this is the one outbound path that
        // does not dial through the SSRF-safe connect callback. The operator's own global relay is
        // deliberately exempt — an in-cluster smarthost on a private address is the normal shape,
        // and it comes from configuration rather than from a tenant.
        if (orgConfig != null
            && await SmtpEndpointValidator.ValidateAsync(orgConfig.Host, orgConfig.Port, orgConfig.StartTls) is { } orgSmtpError)
        {
            throw new InvalidOperationException($"org smtp endpoint refused: {orgSmtpError}");
        }

        if (orgConfig != null)
        {
            host        = orgConfig.Host;
            port        = orgConfig.Port;
            startTls    = orgConfig.StartTls;
            username    = orgConfig.Username;
            password    = orgConfig.PasswordEnc != null
                ? Encoding.UTF8.GetString(TotpEncryption.Decrypt(
                    appConfig.SmtpEncKey, orgConfig.PasswordEnc))
                : null;
            fromAddress = orgConfig.FromAddress;
            fromName    = orgConfig.FromName;
        }
        else if (!string.IsNullOrEmpty(appConfig.SmtpHost))
        {
            host        = appConfig.SmtpHost;
            port        = appConfig.SmtpPort;
            startTls    = appConfig.SmtpStartTls;
            username    = appConfig.SmtpUsername;
            password    = appConfig.SmtpPassword;
            fromAddress = appConfig.SmtpFromAddress;
            fromName    = appConfig.SmtpFromName;
        }
        else
        {
            logger.LogWarning("[EMAIL NO-OP] No SMTP configured. Invite To={To} Org={Org}", to, orgName);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = $"You've been invited to {orgName}";
        message.Body = new TextPart("plain")
        {
            Text = $"You have been invited to join {orgName}.\n\nClick the link below to accept your invitation and set your password:\n\n{inviteUrl}\n\nThis link expires in {appConfig.InviteExpiryHours} hours."
        };

        await SmtpSendAsync(host, port, startTls, username, password, message);
    }

    public async Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt, Guid? orgId = null)
    {
        var orgConfig = orgId.HasValue
            ? await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == orgId.Value)
            : null;

        string host; int port; bool startTls; string? username; string? password; string fromAddress; string fromName;
        // A tenant-supplied relay is re-validated every time it is used, not only when it was
        // saved: DNS is the tenant's to change afterwards, and this is the one outbound path that
        // does not dial through the SSRF-safe connect callback. The operator's own global relay is
        // deliberately exempt — an in-cluster smarthost on a private address is the normal shape,
        // and it comes from configuration rather than from a tenant.
        if (orgConfig != null
            && await SmtpEndpointValidator.ValidateAsync(orgConfig.Host, orgConfig.Port, orgConfig.StartTls) is { } orgSmtpError)
        {
            throw new InvalidOperationException($"org smtp endpoint refused: {orgSmtpError}");
        }

        if (orgConfig != null)
        {
            host = orgConfig.Host; port = orgConfig.Port; startTls = orgConfig.StartTls;
            username = orgConfig.Username;
            password = orgConfig.PasswordEnc != null
                ? Encoding.UTF8.GetString(TotpEncryption.Decrypt(appConfig.SmtpEncKey, orgConfig.PasswordEnc))
                : null;
            fromAddress = orgConfig.FromAddress; fromName = orgConfig.FromName;
        }
        else if (!string.IsNullOrEmpty(appConfig.SmtpHost))
        {
            host = appConfig.SmtpHost; port = appConfig.SmtpPort; startTls = appConfig.SmtpStartTls;
            username = appConfig.SmtpUsername; password = appConfig.SmtpPassword;
            fromAddress = appConfig.SmtpFromAddress; fromName = appConfig.SmtpFromName;
        }
        else
        {
            logger.LogWarning("[EMAIL NO-OP] No SMTP configured for new-device alert To={To}", to);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = "New device login detected";
        message.Body = new TextPart("plain")
        {
            Text = $"A new device logged into your account at {loginAt:R}.\n\nIP address: {ipAddress}\nDevice: {userAgent}\n\nIf this was not you, please reset your password immediately."
        };

        await SmtpSendAsync(host, port, startTls, username, password, message);
    }

    public async Task CheckConnectivityAsync()
    {
        if (string.IsNullOrEmpty(appConfig.SmtpHost))
            throw new InvalidOperationException("SMTP not configured");
        await SmtpSendAsync(appConfig.SmtpHost, appConfig.SmtpPort, appConfig.SmtpStartTls,
            appConfig.SmtpUsername, appConfig.SmtpPassword, message: null);
    }

    private static async Task SmtpSendAsync(
        string host, int port, bool startTls,
        string? username, string? password,
        MimeMessage? message)
    {
        using var client = new SmtpClient();
        // Port 465 is implicit TLS: the socket is TLS from the first byte, and StartTls on it
        // negotiates nothing. Without this branch a config saved as "465, start_tls off" — which
        // is the correct way to describe SMTPS — sent credentials in the clear.
        var security = SecureSocketOptions.None;
        if (port == SmtpEndpointValidator.ImplicitTlsPort) security = SecureSocketOptions.SslOnConnect;
        else if (startTls) security = SecureSocketOptions.StartTls;
        await client.ConnectAsync(host, port, security);
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            await client.AuthenticateAsync(username, password);
        if (message != null)
            await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}

// ── SMS ────────────────────────────────────────────────────────────────────

public interface ISmsService
{
    Task SendOtpAsync(string to, string code, string purpose);

    /// <summary>
    /// False when no real SMS provider is wired up. Callers MUST check this before offering SMS
    /// as a factor: the stub silently drops messages, so a user whose only second factor is SMS
    /// would be told to enter a code that never arrives and be locked out.
    /// </summary>
    bool IsConfigured { get; }
}

public class StubSmsService(ILogger<StubSmsService> logger) : ISmsService
{
    public bool IsConfigured => false;

    public Task SendOtpAsync(string to, string code, string purpose)
    {
        logger.LogWarning("[STUB SMS] To={To} Purpose={Purpose}", to, purpose);
        return Task.CompletedTask;
    }
}
