using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RediensIAM.Data;

/// <summary>
/// Design-time only: what <c>dotnet ef migrations</c> uses to build the model. The running
/// application never goes through here — it resolves its connection string through
/// <see cref="Config.AppConfig"/>.
/// </summary>
public class RediensIamDbContextFactory : IDesignTimeDbContextFactory<RediensIamDbContext>
{
    /// <summary>
    /// The environment variable the developer sets, and the only source of credentials.
    ///
    /// <para>
    /// There used to be a fallback literal ending in <c>Password=postgres</c>, with a suppression
    /// beside it explaining that it never reaches production. That was true and beside the point: a
    /// password in source is a password in source, it is what people copy, and the suppression was
    /// there to stop a scanner saying so. Asking for the variable costs a developer one export and
    /// removes the literal rather than the warning about it.
    /// </para>
    /// </summary>
    private const string ConnectionVariable = "ConnectionStrings__DefaultConnection";

    public RediensIamDbContext CreateDbContext(string[] args)
    {
        var connStr = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new InvalidOperationException(
                $"{ConnectionVariable} is not set. EF's design-time tooling needs a database to " +
                "build the model against — any local PostgreSQL will do, and no migration is " +
                "applied by building it. For example:\n\n" +
                $"  export {ConnectionVariable}=\"Host=localhost;Database=rediensiam;Username=postgres\"\n");
        }

        var opts = new DbContextOptionsBuilder<RediensIamDbContext>()
            .UseNpgsql(connStr)
            .Options;
        return new RediensIamDbContext(opts);
    }
}
