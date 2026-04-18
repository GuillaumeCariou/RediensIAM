using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RediensIAM.Data;

public class RediensIamDbContextFactory : IDesignTimeDbContextFactory<RediensIamDbContext>
{
    public RediensIamDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<RediensIamDbContext>()
            .UseNpgsql("Host=localhost;Database=rediensiam;Username=postgres;Password=postgres")
            .Options;
        return new RediensIamDbContext(opts);
    }
}
