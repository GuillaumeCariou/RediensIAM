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

        // 80, not 64: a keyed hash carries a "k{keyId}:" envelope in front of the 64 hex digits.
        builder.Property(x => x.Hash).IsRequired().HasMaxLength(AuditChain.MaxHashLength).HasDefaultValue("");
        builder.Property(x => x.PrevHash).HasMaxLength(AuditChain.MaxHashLength);

        builder.HasIndex(x => new { x.OrgId, x.CreatedAt });
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        builder.HasIndex(x => new { x.ActorId, x.CreatedAt });
        builder.HasIndex(x => new { x.Action, x.CreatedAt });
        // The chain is walked per organisation in id order; verification would otherwise be a
        // full scan of the table for every org.
        builder.HasIndex(x => new { x.OrgId, x.Id });
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

/// <summary>
/// The shared key/value store, the rate counters, the webhook queue and the DataProtection key
/// ring — everything that used to live in Dragonfly.
///
/// <para>
/// None of these carries an <c>OrgId</c>, and that is the point: they are deployment-wide. They
/// are listed as deployment-global in <c>deploy/rediensiam/files/rls.sql</c>, which <i>raises</i>
/// on a public table that is neither policied nor declared — so adding a table here without
/// declaring it there fails the deploy rather than silently escaping row-level security.
/// </para>
/// </summary>
public class SharedStateConfiguration : IEntityTypeConfiguration<SharedStateEntry>
{
    public void Configure(EntityTypeBuilder<SharedStateEntry> b)
    {
        b.ToTable("shared_state");
        b.HasKey(e => e.Key);
        b.Property(e => e.Key).HasColumnName("key").HasMaxLength(512);
        b.Property(e => e.Value).HasColumnName("value").IsRequired();
        b.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        // Only the sweeper scans by expiry; every read is a primary-key lookup that also filters
        // on it. The index exists for the sweep, not for the hot path.
        b.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_shared_state_expires_at");
    }
}

public class RateCounterConfiguration : IEntityTypeConfiguration<RateCounter>
{
    public void Configure(EntityTypeBuilder<RateCounter> b)
    {
        b.ToTable("rate_counters");
        b.HasKey(e => e.Key);
        b.Property(e => e.Key).HasColumnName("key").HasMaxLength(512);
        b.Property(e => e.Count).HasColumnName("count");
        b.Property(e => e.WindowEnd).HasColumnName("window_end");
        b.HasIndex(e => e.WindowEnd).HasDatabaseName("ix_rate_counters_window_end");
    }
}

public class WebhookPendingConfiguration : IEntityTypeConfiguration<WebhookPending>
{
    public void Configure(EntityTypeBuilder<WebhookPending> b)
    {
        b.ToTable("webhook_pending");
        b.HasKey(e => e.JobJson);
        b.Property(e => e.JobJson).HasColumnName("job_json");
        b.Property(e => e.Score).HasColumnName("score");
        // The dispatcher drains in score order — that is what the Redis sorted set was for.
        b.HasIndex(e => e.Score).HasDatabaseName("ix_webhook_pending_score");
    }
}

public class ImpersonationSessionConfiguration : IEntityTypeConfiguration<ImpersonationSession>
{
    public void Configure(EntityTypeBuilder<ImpersonationSession> b)
    {
        b.ToTable("impersonation_sessions");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        b.Property(e => e.ActorLevel).IsRequired().HasMaxLength(50);
        b.Property(e => e.Mode).IsRequired().HasMaxLength(10);
        b.Property(e => e.Reason).IsRequired().HasMaxLength(500);
        b.Property(e => e.TokenHash).IsRequired().HasMaxLength(64);
        b.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        // The lookup every introspection makes. Unique because two live sessions cannot share a
        // credential, and the database is the right place to say so.
        b.HasIndex(e => e.TokenHash).IsUnique();
        // "Which sessions is this operator running" — asked by the one-at-a-time rule on every
        // open, and by the listing route.
        b.HasIndex(e => new { e.ActorUserId, e.RevokedAt });
        b.HasIndex(e => e.OrgId);
    }
}

public class DataProtectionKeyConfiguration
    : IEntityTypeConfiguration<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey>
{
    public void Configure(
        EntityTypeBuilder<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> b)
    {
        b.ToTable("data_protection_keys");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasColumnName("id");
        b.Property(e => e.FriendlyName).HasColumnName("friendly_name");
        b.Property(e => e.Xml).HasColumnName("xml");
    }
}
