using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RediensIAM.Data.Entities;

namespace RediensIAM.Data.Configurations;

public class UserListConfiguration : IEntityTypeConfiguration<UserList>
{
    public void Configure(EntityTypeBuilder<UserList> builder)
    {
        builder.ToTable("user_lists");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Immovable).HasDefaultValue(false);
        builder.Property(x => x.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(x => x.OrgId);

        builder.HasMany(x => x.Users)
               .WithOne(x => x.UserList)
               .HasForeignKey(x => x.UserListId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.ServiceAccounts)
               .WithOne(x => x.UserList)
               .HasForeignKey(x => x.UserListId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Projects)
               .WithOne(x => x.AssignedUserList)
               .HasForeignKey(x => x.AssignedUserListId)
               .OnDelete(DeleteBehavior.SetNull);
    }
}
