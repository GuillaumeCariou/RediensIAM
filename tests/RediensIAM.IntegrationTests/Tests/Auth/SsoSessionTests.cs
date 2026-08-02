using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RediensIAM.IntegrationTests.Infrastructure;

namespace RediensIAM.IntegrationTests.Tests.Auth;

/// <summary>
/// Signing in once has to mean something.
///
/// <para>
/// <c>AcceptLoginAsync</c> sent <c>remember = false, remember_for = 0</c>, so Hydra created no SSO
/// session at all: every authorization request needed the password again. Refreshing the console
/// asked for it. Opening a second tenant application asked for it. It read as a cookie problem —
/// the console and the issuer are different origins — and it was not: nothing had ever asked Hydra
/// to remember anybody.
/// </para>
///
/// <para>
/// It is now a bounded session rather than an unconditional one. <c>Security:SsoSessionMinutes</c>
/// sets the lifetime, and <b>zero restores the old behaviour</b> — a deployment that wants a
/// password at every authorization can still have it, deliberately rather than by accident.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class SsoSessionTests(TestFixture fixture)
{
    private static JsonElement AcceptBody(string? body)
    {
        body.Should().NotBeNull("the stub recorded no accept-login call");
        return JsonDocument.Parse(body!).RootElement;
    }

    private RediensIAM.Services.HydraService HydraWith(int? ssoMinutes)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Hydra:AdminUrl"] = fixture.Hydra.Url,
            ["ConnectionStrings:Default"] = "Host=unused",
        };
        if (ssoMinutes.HasValue) settings["Security:SsoSessionMinutes"] = ssoMinutes.Value.ToString();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings).Build();
        var appConfig = new RediensIAM.Config.AppConfig(config);

        using var scope = fixture.Services.CreateScope();
        return new RediensIAM.Services.HydraService(
            scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
            appConfig,
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
    }

    [Fact]
    public async Task AcceptLogin_AsksHydraToRememberTheUser()
    {
        fixture.Hydra.ResetDefaults();
        var hydra = HydraWith(null);   // the shipped default

        await hydra.AcceptLoginAsync(Guid.NewGuid().ToString("N"), "user:1", []);

        var body = AcceptBody(fixture.Hydra.LastRequestBody("/login/accept", "PUT"));
        body.GetProperty("remember").GetBoolean().Should().BeTrue(
            "without this Hydra keeps no session and every authorization request needs the password again");
        body.GetProperty("remember_for").GetInt32().Should().BeGreaterThan(0,
            "a session Hydra is told to remember for zero seconds is not remembered");
    }

    [Fact]
    public async Task AcceptLogin_HonoursTheConfiguredLifetime()
    {
        fixture.Hydra.ResetDefaults();
        var hydra = HydraWith(90);

        await hydra.AcceptLoginAsync(Guid.NewGuid().ToString("N"), "user:2", []);

        AcceptBody(fixture.Hydra.LastRequestBody("/login/accept", "PUT"))
            .GetProperty("remember_for").GetInt32().Should().Be(90 * 60, "the value is minutes, the wire is seconds");
    }

    /// <summary>
    /// Zero is the escape hatch, and it has to keep working: a deployment that wants no SSO session
    /// is making a defensible choice, and it should not have to fork the code to make it.
    /// </summary>
    [Fact]
    public async Task AcceptLogin_WithZeroMinutes_KeepsAskingForThePassword()
    {
        fixture.Hydra.ResetDefaults();
        var hydra = HydraWith(0);

        await hydra.AcceptLoginAsync(Guid.NewGuid().ToString("N"), "user:3", []);

        var body = AcceptBody(fixture.Hydra.LastRequestBody("/login/accept", "PUT"));
        body.GetProperty("remember").GetBoolean().Should().BeFalse();
        body.GetProperty("remember_for").GetInt32().Should().Be(0);
    }
}
