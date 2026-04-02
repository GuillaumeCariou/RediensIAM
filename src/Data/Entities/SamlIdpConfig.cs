namespace RediensIAM.Data.Entities;

public class SamlIdpConfig
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string EntityId { get; set; } = "";
    public string? MetadataUrl { get; set; }
    public string? SsoUrl { get; set; }
    public string? CertificatePem { get; set; }
    public string EmailAttributeName { get; set; } = "email";
    public string? DisplayNameAttributeName { get; set; }
    public bool JitProvisioning { get; set; } = true;
    public Guid? DefaultRoleId { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Project Project { get; set; } = null!;
    public Role? DefaultRole { get; set; }
}
