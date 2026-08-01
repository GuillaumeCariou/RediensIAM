using Microsoft.Extensions.Configuration;
using RediensIAM.Config;

namespace RediensIAM.IntegrationTests.Tests.Regression;

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

    // ── Security:RequireAdminMfa ──────────────────────────────────────────────

    /// <summary>
    /// Unset must mean "do not gate the login". The default used to be on, and on a first launch
    /// that is a lockout: the only account is the bootstrap admin, and enrolment is what it cannot
    /// finish until it reaches the console and configures SMTP or SMS. The console reminds instead
    /// (<c>MfaReminder</c>), and <c>values.prod.yaml</c> turns enforcement back on.
    /// </summary>
    [Fact]
    public void RequireAdminMfa_WhenUnset_DoesNotGateTheLogin()
    {
        Build().RequireAdminMfa.Should().BeFalse();
    }

    /// <summary>Off by default is only defensible while turning it on still works.</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void RequireAdminMfa_IsHonoured(string configured, bool expected)
    {
        Build(("Security:RequireAdminMfa", configured)).RequireAdminMfa.Should().Be(expected);
    }

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

    /// <summary>Top-level live keys, which have no section to look inside.</summary>
    [Theory]
    [InlineData("IAM_PUBLIC_PORT")]
    [InlineData("IAM_ADMIN_PORT")]
    [InlineData("IAM_ADMIN_PATH")]
    [InlineData("AllowedHosts")]
    [InlineData("Logging")]
    public void LiveTopLevelKeys_AreStillDeclared(string key)
    {
        ShippedAppSettings().ContainsKey(key).Should().BeTrue($"{key} is read by the application");
    }
}
