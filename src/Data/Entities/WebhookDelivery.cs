namespace RediensIAM.Data.Entities;

public class WebhookDelivery
{
    public Guid Id { get; set; }
    public Guid WebhookId { get; set; }
    public string Event { get; set; } = "";
    public string Payload { get; set; } = "";
    public int? StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Webhook Webhook { get; set; } = null!;
}
