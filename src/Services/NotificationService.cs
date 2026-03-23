using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

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

public class SmtpEmailService(IConfiguration config) : IEmailService
{
    public async Task SendOtpAsync(string to, string code, string purpose)
    {
        var subject = purpose switch
        {
            "registration"   => "Your verification code",
            "password_reset" => "Your password reset code",
            _                => "Your verification code"
        };

        var body = $"Your {subject.ToLower()} is: {code}\n\nThis code expires in 10 minutes.";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            config["Smtp:FromName"] ?? "RediensIAM",
            config["Smtp:FromAddress"] ?? "noreply@localhost"));
        message.To.Add(new MailboxAddress("", to));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        var host = config["Smtp:Host"]!;
        var port = config.GetValue<int>("Smtp:Port", 587);
        var startTls = config.GetValue<bool>("Smtp:StartTls", true);

        await client.ConnectAsync(host, port, startTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto);

        var username = config["Smtp:Username"];
        var password = config["Smtp:Password"];
        if (!string.IsNullOrEmpty(username))
            await client.AuthenticateAsync(username, password);

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
