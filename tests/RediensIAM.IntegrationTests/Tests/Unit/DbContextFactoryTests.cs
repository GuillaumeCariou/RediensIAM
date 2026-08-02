using RediensIAM.Data;

namespace RediensIAM.IntegrationTests.Tests.Unit;

/// <summary>
/// The design-time factory <c>dotnet ef migrations</c> uses. The running application never goes
/// through here, but it is still shipped code: it once carried a fallback connection string
/// ending in <c>Password=postgres</c>, with a suppression beside it explaining that it never
/// reaches production. It reads the environment and nothing else now, and the message it prints
/// when the variable is missing is the whole user interface of the thing.
///
/// No fixture: it never opens a connection, it only builds the options.
/// </summary>
public sealed class DbContextFactoryTests : IDisposable
{
    private const string Variable = "ConnectionStrings__DefaultConnection";
    private readonly string? original = Environment.GetEnvironmentVariable(Variable);

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, original);

    [Fact]
    public void It_builds_a_context_from_the_environment()
    {
        Environment.SetEnvironmentVariable(
            Variable, "Host=localhost;Database=rediensiam;Username=postgres");

        using var db = new RediensIamDbContextFactory().CreateDbContext([]);

        Assert.NotNull(db);
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void It_refuses_to_guess_when_the_variable_is_not_set(string? value)
    {
        Environment.SetEnvironmentVariable(Variable, value);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new RediensIamDbContextFactory().CreateDbContext([]));

        // The message is the interface: it has to name the variable and show a usable example,
        // or the next person adds a fallback literal back.
        Assert.Contains(Variable, ex.Message);
        Assert.Contains("export", ex.Message);
    }
}
