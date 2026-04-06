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
    Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null);
    Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt);
    /// <summary>Connect, optionally authenticate, then disconnect. Throws on failure.</summary>
    Task CheckConnectivityAsync();
}

public class StubEmailService(ILogger<StubEmailService> logger) : IEmailService
{
    public Task SendOtpAsync(string to, string code, string purpose, Guid? orgId = null, Guid? projectId = null)
    {
        logger.LogWarning("[STUB EMAIL] To={To} Purpose={Purpose} Code={Code}", to, purpose, code);
        return Task.CompletedTask;
    }

    public Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null)
    {
        logger.LogWarning("[STUB EMAIL] Invite To={To} Org={Org} Url={Url}", to, orgName, inviteUrl);
        return Task.CompletedTask;
    }

    public Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt)
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

        if (orgConfig != null)
        {
            host        = orgConfig.Host;
            port        = orgConfig.Port;
            startTls    = orgConfig.StartTls;
            username    = orgConfig.Username;
            password    = orgConfig.PasswordEnc != null
                ? Encoding.UTF8.GetString(TotpEncryption.Decrypt(
                    Convert.FromHexString(appConfig.TotpSecretEncryptionKey), orgConfig.PasswordEnc))
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
            logger.LogWarning("[EMAIL NO-OP] No SMTP configured. To={To} Purpose={Purpose} Code={Code}", to, purpose, code);
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
            Text = $"Your {subject.ToLower()} is: {code}\n\nThis code expires in 10 minutes."
        };

        // ── Send ─────────────────────────────────────────────────────────────
        using var client = new SmtpClient();
        await client.ConnectAsync(host, port,
            startTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);

        if (!string.IsNullOrEmpty(username))
            await client.AuthenticateAsync(username, password);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public async Task SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId = null)
    {
        // ── Resolve SMTP config ──────────────────────────────────────────────
        string? host;
        int port;
        bool startTls;
        string? username;
        string? password;
        string fromAddress;
        string fromName;

        Guid? resolvedOrgId = projectId.HasValue
            ? await db.Projects.Where(p => p.Id == projectId.Value).Select(p => p.OrgId).FirstOrDefaultAsync()
            : null;
        var orgConfig = resolvedOrgId is Guid orgIdFromProject
            ? await db.OrgSmtpConfigs.FirstOrDefaultAsync(c => c.OrgId == orgIdFromProject)
            : null;

        if (orgConfig != null)
        {
            host        = orgConfig.Host;
            port        = orgConfig.Port;
            startTls    = orgConfig.StartTls;
            username    = orgConfig.Username;
            password    = orgConfig.PasswordEnc != null
                ? Encoding.UTF8.GetString(TotpEncryption.Decrypt(
                    Convert.FromHexString(appConfig.TotpSecretEncryptionKey), orgConfig.PasswordEnc))
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
            Text = $"You have been invited to join {orgName}.\n\nClick the link below to accept your invitation and set your password:\n\n{inviteUrl}\n\nThis link expires in 72 hours."
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(host, port,
            startTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);

        if (!string.IsNullOrEmpty(username))
            await client.AuthenticateAsync(username, password);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public async Task SendNewDeviceAlertAsync(string to, string ipAddress, string userAgent, DateTimeOffset loginAt)
    {
        if (string.IsNullOrEmpty(appConfig.SmtpHost)) return;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(appConfig.SmtpFromName, appConfig.SmtpFromAddress));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = "New device login detected";
        message.Body = new TextPart("plain")
        {
            Text = $"A new device logged into your account at {loginAt:R}.\n\nIP address: {ipAddress}\nDevice: {userAgent}\n\nIf this was not you, please reset your password immediately."
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(appConfig.SmtpHost, appConfig.SmtpPort,
            appConfig.SmtpStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        if (!string.IsNullOrEmpty(appConfig.SmtpUsername))
            await client.AuthenticateAsync(appConfig.SmtpUsername, appConfig.SmtpPassword);
        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public async Task CheckConnectivityAsync()
    {
        if (string.IsNullOrEmpty(appConfig.SmtpHost))
            throw new InvalidOperationException("SMTP not configured");
        using var client = new SmtpClient();
        await client.ConnectAsync(appConfig.SmtpHost, appConfig.SmtpPort,
            appConfig.SmtpStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);
        if (!string.IsNullOrEmpty(appConfig.SmtpUsername))
            await client.AuthenticateAsync(appConfig.SmtpUsername, appConfig.SmtpPassword);
        await client.DisconnectAsync(true);
    }
}

// ── SMS ────────────────────────────────────────────────────────────────────

public interface ISmsService
{
    Task SendOtpAsync(string to, string code, string purpose);
}

public class StubSmsService(ILogger<StubSmsService> logger) : ISmsService
{
    public Task SendOtpAsync(string to, string code, string purpose)
    {
        logger.LogWarning("[STUB SMS] To={To} Purpose={Purpose} Code={Code}", to, purpose, code);
        return Task.CompletedTask;
    }
}
