using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using RediensIAM.Config;

namespace RediensIAM.Services;

// ── Email ──────────────────────────────────────────────────────────────────

public interface IEmailService
{
    Task SendOtpAsync(string to, string code, string purpose);
}

public class StubEmailService(ILogger<StubEmailService> logger) : IEmailService
{
    public Task SendOtpAsync(string to, string code, string purpose)
    {
        logger.LogWarning("[STUB EMAIL] To={To} Purpose={Purpose} Code={Code}", to, purpose, code);
        return Task.CompletedTask;
    }
}

public class SmtpEmailService(AppConfig appConfig) : IEmailService
{
    public async Task SendOtpAsync(string to, string code, string purpose)
    {
        var subject = purpose switch
        {
            "registration"   => "Your verification code",
            "password_reset" => "Your password reset code",
            _                => "Your verification code"
        };

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(appConfig.SmtpFromName, appConfig.SmtpFromAddress));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = subject;
        message.Body = new TextPart("plain")
        {
            Text = $"Your {subject.ToLower()} is: {code}\n\nThis code expires in 10 minutes."
        };

        using var client = new SmtpClient();
        await client.ConnectAsync(appConfig.SmtpHost!, appConfig.SmtpPort,
            appConfig.SmtpStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);

        if (!string.IsNullOrEmpty(appConfig.SmtpUsername))
            await client.AuthenticateAsync(appConfig.SmtpUsername, appConfig.SmtpPassword);

        await client.SendAsync(message);
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
