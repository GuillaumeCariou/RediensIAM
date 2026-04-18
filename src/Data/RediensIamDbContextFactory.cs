using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RediensIAM.Data;

public class RediensIamDbContextFactory : IDesignTimeDbContextFactory<RediensIamDbContext>
{
    public RediensIamDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=rediensiam;Username=postgres;Password=postgres"; // design-time fallback
#pragma warning disable S2068
        var opts = new DbContextOptionsBuilder<RediensIamDbContext>()
            .UseNpgsql(connStr)
            .Options;
#pragma warning restore S2068
        return new RediensIamDbContext(opts);
    }
}
