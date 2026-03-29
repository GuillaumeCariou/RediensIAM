namespace RediensIAM.Data.Entities;

public class OrgSmtpConfig
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public string? Username { get; set; }
    public string? PasswordEnc { get; set; }   // AES-256-GCM via TotpEncryption
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Organisation Organisation { get; set; } = null!;
}
