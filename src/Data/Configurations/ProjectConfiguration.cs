using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RediensIAM.Entities;

namespace RediensIAM.Data.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Slug).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Active).HasDefaultValue(true);
        builder.Property(x => x.RequireRoleToLogin).HasDefaultValue(false);
        builder.Property(x => x.AllowSelfRegistration).HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(x => new { x.OrgId, x.Slug }).IsUnique();
        builder.HasIndex(x => x.AssignedUserListId);

        builder.Property(x => x.LoginTheme)
               .HasColumnType("jsonb")
               .HasConversion(
                   v => JsonSerializer.Serialize(v, JsonOptions),
                   v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, JsonOptions) ?? new Dictionary<string, object>());

        builder.Property(x => x.AllowedEmailDomains).HasColumnType("text[]");

        builder.HasOne(x => x.DefaultRole).WithMany().HasForeignKey(x => x.DefaultRoleId).OnDelete(DeleteBehavior.SetNull);
        builder.HasMany(x => x.Roles).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.UserProjectRoles).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(x => x.ServiceAccountProjectRoles).WithOne(x => x.Project).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}
