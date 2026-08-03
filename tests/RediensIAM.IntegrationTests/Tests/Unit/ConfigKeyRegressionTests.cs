using Microsoft.Extensions.Configuration;
using RediensIAM.Config;

namespace RediensIAM.IntegrationTests.Tests.Unit;

/// <summary>
/// Configuration keys that lied. <c>Database:MigrateOnStartup</c> was declared and read by nothing,
/// so an operator who set it to false still got migrations applied; four further keys were declared
/// and read by nothing at all, which is how a reader ends up believing a setting exists.
///
/// No fixture: these are all pure configuration binding, so they need neither a database nor Redis.
/// </summary>
public class ConfigKeyRegressionTests
{
    private static AppConfig Build(params (string Key, string Value)[] entries) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build());

    // ── Security:RequireAdminMfa — removed, deliberately ─────────────────────
    //
    // Two tests lived here: one pinned the default to false, the other pinned that setting it to
    // true was honoured. Both described a key that no longer exists. It was removed rather than
    // re-defaulted because its correct value was never a preference — it changed by itself once
    // the deployment was configured, and it was dangerous in both directions. The behaviour it
    // used to gate is now derived, and its tests live in Tests/Auth/AdminMfaBootstrapTests.cs.

    // ── Database:MigrateOnStartup ─────────────────────────────────────────────

    /// <summary>
    /// Unset must keep meaning "migrate", which is what every existing deployment already does.
    /// Honouring the key is only safe because the default did not move.
    /// </summary>
    [Fact]
    public void MigrateOnStartup_WhenUnset_DefaultsToMigrating()
    {
        Build().MigrateOnStartup.Should().BeTrue();
    }

    /// <summary>
    /// The actual defect: the key was documented, shipped set to true, and read by nothing, so
    /// setting it to false froze nothing and the operator had no way to tell.
    /// </summary>
    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    public void MigrateOnStartup_IsHonoured(string configured, bool expected)
    {
        Build(("Database:MigrateOnStartup", configured))
            .MigrateOnStartup.Should().Be(expected);
    }

    // ── Retired keys ──────────────────────────────────────────────────────────

    private static JsonObject ShippedAppSettings()
    {
        // The copy in the test output is the file the application itself loads.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(path).Should().BeTrue("appsettings.json is copied to the test output");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    /// <summary>
    /// Each of these was declared in appsettings.json and read by nothing in <c>src/</c> — no
    /// direct lookup, no options class bound to its section, no differently-spelled accessor. A
    /// configuration key that nothing reads is worse than absent: it reads as a supported setting.
    /// </summary>
    [Theory]
    [InlineData("Cache", "ProjectTtlMinutes")]
    [InlineData("Cache", "JwksTtlMinutes")]
    [InlineData("App", "FrontendUrl")]
    [InlineData("App", "LoginPath")]
    public void RetiredKeys_AreNotDeclared(string section, string key)
    {
        ShippedAppSettings()[section]!.AsObject()
            .ContainsKey(key).Should().BeFalse(
                $"{section}:{key} is read by nothing — declaring it advertises a setting that does not exist");
    }

    /// <summary>
    /// The other side of the same sweep: these are live, and deleting one alongside the dead keys
    /// would be a silent behaviour change rather than a cleanup.
    /// </summary>
    [Theory]
    [InlineData("Cache", "PatTtlMinutes")]
    [InlineData("Security", "Argon2Pepper")]
    [InlineData("App", "PublicUrl")]
    [InlineData("App", "AdminSpaOrigin")]
    [InlineData("App", "TrustedProxies")]
    [InlineData("Database", "MigrateOnStartup")]
    public void LiveKeys_AreStillDeclared(string section, string key)
    {
        ShippedAppSettings()[section]!.AsObject()
            .ContainsKey(key).Should().BeTrue($"{section}:{key} is read by the application");
    }

    /// <summary>
    /// Top-level live keys, which have no section to look inside.
    ///
    /// <para>
    /// IAM_ADMIN_PATH used to be here, asserted as "read by the application", and it was not: it
    /// travelled from appsettings to an env var to a database column that nothing consulted. Where
    /// the console is served is the compile-time constant <c>Roles.ConsoleBasePath</c>. A test that
    /// keeps a dead key alive is worse than no test — it makes the key look load-bearing.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("IAM_PUBLIC_PORT")]
    [InlineData("IAM_ADMIN_PORT")]
    [InlineData("AllowedHosts")]
    [InlineData("Logging")]
    public void LiveTopLevelKeys_AreStillDeclared(string key)
    {
        ShippedAppSettings().ContainsKey(key).Should().BeTrue($"{key} is read by the application");
    }
}
