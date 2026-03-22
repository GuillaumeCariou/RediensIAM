using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RediensIAM.Entities;

namespace RediensIAM.Data.Configurations;

public class OrganisationConfiguration : IEntityTypeConfiguration<Organisation>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Configure(EntityTypeBuilder<Organisation> builder)
    {
        builder.ToTable("organisations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Slug).IsRequired().HasMaxLength(100);
        builder.HasIndex(x => x.Slug).IsUnique();
        builder.Property(x => x.Active).HasDefaultValue(true);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");

        builder.Property(x => x.Metadata)
               .HasColumnType("jsonb")
               .HasConversion(
                   v => JsonSerializer.Serialize(v, JsonOptions),
                   v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, JsonOptions) ?? new Dictionary<string, object>());

        builder.HasOne(x => x.OrgList)
               .WithMany()
               .HasForeignKey(x => x.OrgListId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.UserLists)
               .WithOne(x => x.Organisation)
               .HasForeignKey(x => x.OrgId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Projects)
               .WithOne(x => x.Organisation)
               .HasForeignKey(x => x.OrgId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.OrgRoles)
               .WithOne(x => x.Organisation)
               .HasForeignKey(x => x.OrgId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
