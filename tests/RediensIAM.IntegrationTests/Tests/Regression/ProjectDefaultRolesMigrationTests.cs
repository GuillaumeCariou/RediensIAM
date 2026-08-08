using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using RediensIAM.Data.Migrations;

namespace RediensIAM.IntegrationTests.Tests.Regression;

/// <summary>
/// La migration qui fait passer le rôle par défaut d'un projet d'une clé étrangère unique
/// (<c>projects.DefaultRoleId</c>) à un drapeau par rôle (<c>roles.IsDefault</c>).
///
/// <para>
/// Ce qu'elle peut casser n'est pas visible à la compilation : l'échafaudage produit par
/// <c>dotnet ef</c> supprimait la colonne <i>avant</i> d'ajouter le drapeau, et le réglage de
/// chaque locataire partait avec elle sans que rien ne le consigne. Ces tests tiennent donc les
/// deux moitiés de la garantie — l'ordre des opérations, et ce que le report écrit réellement.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class ProjectDefaultRolesMigrationTests(TestFixture fixture)
{
    private static readonly IReadOnlyList<MigrationOperation> Up =
        ((Migration)new ProjectDefaultRoles()).UpOperations;

    private static string BackfillSql => Up.OfType<SqlOperation>().Single().Sql;

    /// <summary>
    /// L'ordre <b>est</b> la migration : reporter après la suppression ne reporte rien, et un
    /// report qui ne lit plus la colonne qu'il traduit ne peut pas échouer bruyamment.
    /// </summary>
    [Fact]
    public void Up_BackfillsBetweenAddingTheFlagAndDroppingTheColumn()
    {
        var addFlag = Up.ToList().FindIndex(o => o is AddColumnOperation { Table: "roles", Name: "IsDefault" });
        var backfill = Up.ToList().FindIndex(o => o is SqlOperation);
        var dropColumn = Up.ToList().FindIndex(o => o is DropColumnOperation { Table: "projects", Name: "DefaultRoleId" });

        addFlag.Should().BeGreaterThanOrEqualTo(0);
        backfill.Should().BeGreaterThan(addFlag);
        dropColumn.Should().BeGreaterThan(backfill);
    }

    /// <summary>
    /// Le SQL du report lui-même, joué sur un schéma de la forme d'avant. Il est lu depuis la
    /// migration et non recopié : une copie dans le test passerait au vert sur une migration
    /// fausse.
    /// </summary>
    [Fact]
    public async Task Backfill_FlagsTheRoleEachProjectHadAsItsDefault_AndNothingElse()
    {
        var schema = "mig_" + Guid.NewGuid().ToString("N")[..12];
        var chosen = Guid.NewGuid();
        var other = Guid.NewGuid();
        var orphan = Guid.NewGuid();

        await using var conn = new NpgsqlConnection(fixture.PostgresConnectionString);
        await conn.OpenAsync();
        try
        {
            await ExecAsync(conn, $"""
                CREATE SCHEMA "{schema}";
                SET search_path TO "{schema}";
                CREATE TABLE roles ("Id" uuid PRIMARY KEY, "ProjectId" uuid, "Rank" int, "IsDefault" boolean NOT NULL DEFAULT false);
                CREATE TABLE projects ("Id" uuid PRIMARY KEY, "DefaultRoleId" uuid);
                INSERT INTO roles ("Id", "ProjectId", "Rank") VALUES
                    ('{chosen}', '{Guid.Empty}', 100),
                    ('{other}',  '{Guid.Empty}', 1),
                    ('{orphan}', '{Guid.NewGuid()}', 100);
                INSERT INTO projects ("Id", "DefaultRoleId") VALUES
                    ('{Guid.Empty}', '{chosen}'),
                    ('{Guid.NewGuid()}', NULL);
                """);

            await ExecAsync(conn, $"SET search_path TO \"{schema}\"; {BackfillSql}");

            (await FlagOf(conn, schema, chosen)).Should().BeTrue("le défaut configuré doit survivre");
            (await FlagOf(conn, schema, other)).Should().BeFalse();
            (await FlagOf(conn, schema, orphan)).Should().BeFalse();
        }
        finally
        {
            await ExecAsync(conn, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;");
        }
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> FlagOf(NpgsqlConnection conn, string schema, Guid roleId)
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT \"IsDefault\" FROM \"{schema}\".roles WHERE \"Id\" = @id", conn);
        cmd.Parameters.AddWithValue("id", roleId);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
