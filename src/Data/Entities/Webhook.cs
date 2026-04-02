namespace RediensIAM.Data.Entities;

public class Webhook
{
    public Guid Id { get; set; }
    public Guid? OrgId { get; set; }      // null = system-level
    public Guid? ProjectId { get; set; }  // null = org-level
    public string Url { get; set; } = "";
    public string SecretEnc { get; set; } = ""; // AES-256-GCM encrypted
    public string[] Events { get; set; } = [];
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<WebhookDelivery> Deliveries { get; set; } = [];
}
