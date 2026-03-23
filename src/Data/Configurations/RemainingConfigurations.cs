using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RediensIAM.Entities;

namespace RediensIAM.Data.Configurations;

public class ServiceAccountConfiguration : IEntityTypeConfiguration<ServiceAccount>
{
    public void Configure(EntityTypeBuilder<ServiceAccount> builder)
    {
        builder.ToTable("service_accounts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.IsSystem).HasDefaultValue(false);
        builder.Property(x => x.Active).HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.UserListId, x.Name }).IsUnique();
        builder.HasIndex(x => x.UserListId);
        builder.HasMany(x => x.PersonalAccessTokens).WithOne(x => x.ServiceAccount).HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.ProjectRoles).WithOne(x => x.ServiceAccount).HasForeignKey(x => x.SaId).OnDelete(DeleteBehavior.Cascade);
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

public class ServiceAccountProjectRoleConfiguration : IEntityTypeConfiguration<ServiceAccountProjectRole>
{
    public void Configure(EntityTypeBuilder<ServiceAccountProjectRole> builder)
    {
        builder.ToTable("service_account_project_roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Role).IsRequired().HasMaxLength(100);
        builder.Property(x => x.GrantedAt).HasDefaultValueSql("now()");
        builder.HasIndex(x => new { x.SaId, x.ProjectId, x.Role }).IsUnique();
        builder.HasIndex(x => x.ProjectId);
        builder.HasIndex(x => x.SaId);
    }
}

public class ServiceAccountOrgRoleConfiguration : IEntityTypeConfiguration<ServiceAccountOrgRole>
{
    public void Configure(EntityTypeBuilder<ServiceAccountOrgRole> builder)
    {
        builder.ToTable("service_account_org_roles");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Role).IsRequired().HasMaxLength(100);
        builder.Property(x => x.GrantedAt).HasDefaultValueSql("now()");
        builder.HasOne(x => x.ServiceAccount).WithMany(x => x.OrgRoles).HasForeignKey(x => x.ServiceAccountId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.ServiceAccountId, x.Role }).IsUnique();
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

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

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
                   v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, JsonOptions) ?? new Dictionary<string, object>());

        builder.HasIndex(x => new { x.OrgId, x.CreatedAt });
        builder.HasIndex(x => new { x.ProjectId, x.CreatedAt });
        builder.HasIndex(x => new { x.ActorId, x.CreatedAt });
        builder.HasIndex(x => new { x.Action, x.CreatedAt });

    }
}
