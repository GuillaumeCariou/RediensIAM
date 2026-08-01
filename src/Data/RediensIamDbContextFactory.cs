using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RediensIAM.Data;

public class RediensIamDbContextFactory : IDesignTimeDbContextFactory<RediensIamDbContext>
{
    public RediensIamDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=rediensiam;Username=postgres;Password=postgres";
        // S2068: the literal above is a local-only fallback for `dotnet ef migrations`, which needs
        // a connection string to build the model and never connects with it in a deployment. The
        // running app resolves its credentials through AppConfig — nothing here reaches production.
#pragma warning disable S2068
        var opts = new DbContextOptionsBuilder<RediensIamDbContext>()
            .UseNpgsql(connStr)
            .Options;
#pragma warning restore S2068
        return new RediensIamDbContext(opts);
    }
}
