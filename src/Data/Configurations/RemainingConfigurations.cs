using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RediensIAM.Data.Entities;

namespace RediensIAM.Data.Configurations;

public class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccount>
{
    public void Configure(EntityTypeBuilder<ServiceAccount> builder)
    {
        builder.ToTable("service_accounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Active).HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.UserListId, x.Name }).IsUnique();
        builder.HasIndex(x => x.UserListId);
        builder.HasMany(x => x.PersonalAccessTokens).WithOne(x => x.ServiceAccount).HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.Roles).WithOne(x => x.ServiceAccount).HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ServiceAccountRoleConfiguration : IEntityTypeConfiguration<ServiceAccountRole>
{
    public void Configure(EntityTypeBuilder<ServiceAccountRole> builder)
    {
        builder.ToTable("service_account_roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Role).IsRequired().HasMaxLength(100);
        builder.Property(x => x.GrantedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.ServiceAccountId, x.Role, x.OrgId, x.ProjectId }).IsUnique();
        builder.HasIndex(x => x.ServiceAccountId);
    }
}

public class PersonalAccessTokenConfiguration : IEntityTypeConfiguration<PersonalAccessToken>
{
    public void Configure(EntityTypeBuilder<PersonalAccessToken> builder)
    {
        builder.ToTable("personal_access_tokens");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.TokenHash).IsRequired();
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.ServiceAccountId);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Rank).HasDefaultValue(100);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        builder.HasMany(x => x.UserProjectRoles).WithOne(x => x.Role).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserProjectRoleConfiguration : IEntityTypeConfiguration<UserProjectRole>
{
    public void Configure(EntityTypeBuilder<UserProjectRole> builder)
    {
        builder.ToTable("user_project_roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.GrantedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.UserId, x.ProjectId, x.RoleId }).IsUnique();
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.ProjectId);
    }
}

public class OrgRoleConfiguration : IEntityTypeConfiguration<OrgRole>
{
    public void Configure(EntityTypeBuilder<OrgRole> builder)
    {
        builder.ToTable("org_roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Role).IsRequired().HasMaxLength(100);
        builder.Property(x => x.GrantedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.OrgId, x.UserId, x.Role, x.ScopeId }).IsUnique();
    }
}


public class WebAuthnCredentialConfiguration : IEntityTypeConfiguration<WebAuthnCredential>
{
    public void Configure(EntityTypeBuilder<WebAuthnCredential> builder)
    {
        builder.ToTable("webauthn_credentials");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.CredentialId).IsRequired();
        builder.Property(x => x.PublicKey).IsRequired();
        builder.HasIndex(x => x.CredentialId).IsUnique();
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

public class BackupCodeConfiguration : IEntityTypeConfiguration<BackupCode>
{
    public void Configure(EntityTypeBuilder<BackupCode> builder)
    {
        builder.ToTable("backup_codes");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.CodeHash).IsRequired();
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

public class EmailTokenConfiguration : IEntityTypeConfiguration<EmailToken>
{
    public void Configure(EntityTypeBuilder<EmailToken> builder)
    {
        builder.ToTable("email_tokens");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Kind).IsRequired().HasMaxLength(50);
        builder.Property(x => x.TokenHash).IsRequired();
        builder.HasIndex(x => x.TokenHash);
        builder.HasIndex(x => x.UserId);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
    }
}

public class UserSocialAccountConfiguration : IEntityTypeConfiguration<UserSocialAccount>
{
    public void Configure(EntityTypeBuilder<UserSocialAccount> builder)
    {
        builder.ToTable("user_social_accounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(100);
        builder.Property(x => x.ProviderUserId).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Email).HasMaxLength(320);
        builder.Property(x => x.LinkedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
        builder.HasIndex(x => x.UserId);
        builder.HasOne(x => x.User).WithMany(x => x.SocialAccounts).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class OrgSmtpConfigConfiguration : IEntityTypeConfiguration<OrgSmtpConfig>
{
    public void Configure(EntityTypeBuilder<OrgSmtpConfig> builder)
    {
        builder.ToTable("org_smtp_configs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Host).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Port).HasDefaultValue(587);
        builder.Property(x => x.StartTls).HasDefaultValue(true);
        builder.Property(x => x.Username).HasMaxLength(500);
        builder.Property(x => x.FromAddress).IsRequired().HasMaxLength(320);
        builder.Property(x => x.FromName).IsRequired().HasMaxLength(200);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => x.OrgId).IsUnique();
        builder.HasOne(x => x.Organisation).WithOne().HasForeignKey<OrgSmtpConfig>(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class WebhookConfiguration : IEntityTypeConfiguration<Webhook>
{
    public void Configure(EntityTypeBuilder<Webhook> builder)
    {
        builder.ToTable("webhooks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Url).IsRequired().HasMaxLength(2000);
        builder.Property(x => x.SecretEnc).IsRequired().HasDefaultValue("");
        builder.Property(x => x.Active).HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.Events).HasColumnType("jsonb");
        builder.HasIndex(x => x.OrgId);
        builder.HasIndex(x => x.ProjectId);
        builder.HasMany(x => x.Deliveries).WithOne(x => x.Webhook).HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Event).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => x.WebhookId);
        builder.HasIndex(x => x.CreatedAt);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly ValueComparer<Dictionary<string, object>> DictComparer = new(
        (l, r) => JsonSerializer.Serialize(l, JsonOptions) == JsonSerializer.Serialize(r, JsonOptions),
        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
        v => JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!);

    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityByDefaultColumn();
        builder.Property(x => x.Action).IsRequired().HasMaxLength(200);
        builder.Property(x => x.TargetType).HasMaxLength(100);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(x => x.Metadata)
               .HasColumnType("jsonb")
               .HasConversion(
                   v => JsonSerializer.Serialize(v, JsonOptions),
                   v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, JsonOptions) ?? new Dictionary<string, object>(),
                   DictComparer);

        builder.HasIndex(x => new { x.OrgId, x.CreatedAt });
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        builder.HasIndex(x => new { x.ActorId, x.CreatedAt });
        builder.HasIndex(x => new { x.Action, x.CreatedAt });
    }
}

public class SamlIdpConfigConfiguration : IEntityTypeConfiguration<SamlIdpConfig>
{
    public void Configure(EntityTypeBuilder<SamlIdpConfig> builder)
    {
        builder.ToTable("saml_idp_configs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(500);
        builder.Property(x => x.MetadataUrl).HasMaxLength(1000);
        builder.Property(x => x.SsoUrl).HasMaxLength(1000);
        builder.Property(x => x.EmailAttributeName).IsRequired().HasMaxLength(200).HasDefaultValue("email");
        builder.Property(x => x.DisplayNameAttributeName).HasMaxLength(200);
        builder.Property(x => x.JitProvisioning).HasDefaultValue(true);
        builder.Property(x => x.Active).HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => x.ProjectId);
        builder.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.DefaultRole).WithMany().HasForeignKey(x => x.DefaultRoleId).OnDelete(DeleteBehavior.SetNull);
    }
}
