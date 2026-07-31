using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RediensIAM.Config;
using RediensIAM.IntegrationTests.Infrastructure;
using StackExchange.Redis;

namespace RediensIAM.IntegrationTests.Tests.Security;

/// <summary>
/// R-15 — encryption at rest for the DataProtection key ring.
///
/// <para>
/// The ring mints session cookies. It lives in Dragonfly, and ASP.NET Core's default behaviour
/// when no <c>ProtectKeysWith*</c> is configured is to write it in the clear and log a warning.
/// Cache TLS does not touch that: it protects the wire, not the stored bytes.
/// </para>
///
/// <para>
/// Two failure modes are worth more than the feature itself, and each has tests here:
/// a protector that is not in place before the ring is first written, and a ring that
/// <i>silently</i> falls back to unprotected on read. The first kills every session on the next
/// deploy; the second means an attacker with cache write access can plant a key of his own and
/// have the application mint cookies with it.
/// </para>
/// </summary>
[Collection("RediensIAM")]
public class KeyRingProtectionTests(TestFixture fixture)
{
    private const string RootA = "11111111111111111111111111111111111111111111111111111111111111a1";
    private const string RootB = "22222222222222222222222222222222222222222222222222222222222222b2";

    // ── The property the whole feature exists for ─────────────────────────────

    [Fact]
    public void Stored_Key_Carries_No_Plaintext_Key_Material()
    {
        var store = new ListXmlRepository();

        using (var host = Protected(store, RootA))
            host.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("payload");

        var xml = store.Dump();
        // `masterKey` is the element that holds the raw AES/HMAC bytes when nothing protects the
        // ring. Its presence is the whole finding.
        xml.Should().NotContain("masterKey");
        xml.Should().Contain(RootKeyXmlEncryptor.ElementName);
    }

    // ── The ordering trap: the protector has to survive a restart ─────────────

    [Fact]
    public void Key_Ring_Round_Trips_Across_A_Restart()
    {
        var store = new ListXmlRepository();
        string ciphertext;

        using (var first = Protected(store, RootA))
            ciphertext = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("alice");

        // A brand-new host over the same stored bytes — a pod restart, or the second replica.
        // This is what a decryptor the activator cannot construct fails: writing works, and the
        // deploy *after* the one that wrote the keys is the outage.
        using var second = Protected(store, RootA);
        second.GetRequiredService<IDataProtectionProvider>().CreateProtector("session")
              .Unprotect(ciphertext).Should().Be("alice");
    }

    [Fact]
    public async Task Key_Ring_In_The_Real_Cache_Round_Trips_Across_A_Restart()
    {
        // The same assertion against the fixture's Dragonfly container and the real
        // StackExchangeRedis repository, because "it works over a List<XElement>" is not the claim.
        var mux = fixture.Services.GetRequiredService<IConnectionMultiplexer>();
        var key = $"test:dataprotection:{Guid.NewGuid():N}";
        try
        {
            string ciphertext;
            using (var first = Protected(b => b.PersistKeysToStackExchangeRedis(mux, key), RootA))
                ciphertext = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("alice");

            var raw = string.Concat((await mux.GetDatabase().ListRangeAsync(key)).Select(v => v.ToString()));
            raw.Should().NotBeEmpty("the ring must actually have been written to the cache");
            raw.Should().NotContain("masterKey");

            using var second = Protected(b => b.PersistKeysToStackExchangeRedis(mux, key), RootA);
            second.GetRequiredService<IDataProtectionProvider>().CreateProtector("session")
                  .Unprotect(ciphertext).Should().Be("alice");
        }
        finally
        {
            await mux.GetDatabase().KeyDeleteAsync(key);
        }
    }

    // ── An unprotected ring is not silently accepted ──────────────────────────

    [Fact]
    public void A_Ring_Written_Before_Protection_Was_Enabled_Is_Refused_Not_Adopted()
    {
        var store = new ListXmlRepository();

        // Exactly what this deployment has in Dragonfly today: keys written with no XmlEncryptor.
        using (var legacy = Unprotected(store, RootA))
            legacy.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("payload");

        using var host = Protected(store, RootA);
        var read = () => host.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("again");

        // DataProtection wraps whatever the repository throws, so the assertion is on the inner
        // exception — and on the remedy being in the message, because "refused to start" without
        // "here is how to move forward" is how an operator ends up re-disabling the control.
        read.Should().Throw<CryptographicException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*stored unencrypted*")
            .WithMessage("*DEL rediensiam:dataprotection:keys*");
    }

    [Fact]
    public void A_Plaintext_Key_Planted_Beside_A_Protected_Ring_Is_Refused()
    {
        // The attack the at-rest encryption would not stop on its own. He cannot read the ring,
        // so he appends one he can read — and without this check the application adopts it and
        // mints cookies he can forge.
        var store = new ListXmlRepository();
        using (var host = Protected(store, RootA))
            host.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("payload");

        var planted = new ListXmlRepository();
        using (var attacker = Unprotected(planted, RootB))
            attacker.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("payload");
        foreach (var element in planted.Dumped())
            store.StoreElement(element, "planted");

        using var victim = Protected(store, RootA);
        var read = () => victim.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("again");

        read.Should().Throw<CryptographicException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*stored unencrypted*");
    }

    [Fact]
    public void Revocation_Records_Are_Left_Alone()
    {
        // Revocations name a dead key id. They carry no secret and are correctly stored in the
        // clear — rejecting every element that is not encrypted would make key revocation an
        // outage, which is the obvious over-tightening of the check above.
        var store = new ListXmlRepository();
        using (var host = Protected(store, RootA))
        {
            host.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("payload");
            host.GetRequiredService<IKeyManager>().RevokeAllKeys(DateTimeOffset.UtcNow.AddMinutes(-5), "test");
        }
        store.Dump().Should().Contain("revocation");

        using var second = Protected(store, RootA);
        var read = () => second.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("again");

        read.Should().NotThrow();
    }

    // ── The key actually protects something ───────────────────────────────────

    [Fact]
    public void A_Deployment_With_A_Different_Root_Cannot_Read_The_Ring()
    {
        // i.e. stealing the Dragonfly dump is not enough — this is the property TLS does not give.
        var store = new ListXmlRepository();
        using (var owner = Protected(store, RootA))
            owner.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("payload");

        using var thief = Protected(store, RootB);
        var read = () => thief.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Unprotect("whatever");

        read.Should().Throw<Exception>();
    }

    [Fact]
    public void Rotating_The_Root_Keeps_The_Existing_Ring_Readable()
    {
        // The ring goes through TotpEncryption, so it inherits the S-10 key-id envelope: every
        // configured root decrypts, only the first encrypts. Retiring a root without this would
        // silently invalidate every session at the next restart.
        var store = new ListXmlRepository();
        string ciphertext;
        using (var before = Protected(store, RootA))
            ciphertext = before.GetRequiredService<IDataProtectionProvider>().CreateProtector("session").Protect("alice");

        using var after = Protected(store, cfg => cfg["Security:EncryptionKeys"] = $"2:{RootB},1:{RootA}");
        after.GetRequiredService<IDataProtectionProvider>().CreateProtector("session")
             .Unprotect(ciphertext).Should().Be("alice");
    }

    [Fact]
    public void Protection_Without_A_Key_Repository_Refuses_To_Start()
    {
        // Silently protecting a ring that lands in the container's ephemeral filesystem would be
        // the worst of both: no persistence, and a report that says the ring is encrypted.
        var services = new ServiceCollection();
        var appConfig = Config(_ => { });
        services.AddSingleton(appConfig);
        services.AddDataProtection().ProtectKeysWithRootKey(appConfig).SetApplicationName("rediensiam");
        using var provider = services.BuildServiceProvider();

        var start = () => provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("p").Protect("x");

        start.Should().Throw<InvalidOperationException>().WithMessage("*PersistKeysTo*");
    }

    [Fact]
    public void The_Decryptor_Is_Constructible_The_Only_Way_DataProtection_Will_Construct_It()
    {
        // Guards the constructor signature directly, so the reason the round-trip test passes is
        // not a coincidence somebody can refactor away. DataProtection's activator accepts a
        // parameterless constructor or one taking IServiceProvider, and nothing else.
        var ctors = typeof(RootKeyXmlDecryptor).GetConstructors();

        ctors.Should().ContainSingle().Which
             .GetParameters().Select(p => p.ParameterType)
             .Should().Equal(typeof(IServiceProvider));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AppConfig Config(Action<Dictionary<string, string?>> tweak)
    {
        var values = new Dictionary<string, string?> { ["Security:TotpSecretEncryptionKey"] = RootA };
        tweak(values);
        return new AppConfig(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static ServiceProvider Protected(IXmlRepository store, string root) =>
        Protected(store, cfg => cfg["Security:TotpSecretEncryptionKey"] = root);

    private static ServiceProvider Protected(IXmlRepository store, Action<Dictionary<string, string?>> tweak) =>
        Protected(b => b.PersistKeysTo(store), tweak);

    private static ServiceProvider Protected(Func<IDataProtectionBuilder, IDataProtectionBuilder> persist, string root) =>
        Protected(persist, cfg => cfg["Security:TotpSecretEncryptionKey"] = root);

    private static ServiceProvider Protected(
        Func<IDataProtectionBuilder, IDataProtectionBuilder> persist, Action<Dictionary<string, string?>> tweak)
    {
        var services = new ServiceCollection();
        var appConfig = Config(tweak);
        services.AddSingleton(appConfig);
        persist(services.AddDataProtection()).ProtectKeysWithRootKey(appConfig).SetApplicationName("rediensiam");
        return services.BuildServiceProvider();
    }

    private static ServiceProvider Unprotected(IXmlRepository store, string root)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Config(cfg => cfg["Security:TotpSecretEncryptionKey"] = root));
        services.AddDataProtection().PersistKeysTo(store).SetApplicationName("rediensiam");
        return services.BuildServiceProvider();
    }

    /// <summary>In-memory stand-in for the Redis repository, so the stored bytes can be read back.</summary>
    private sealed class ListXmlRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];
        public IReadOnlyCollection<XElement> GetAllElements() => [.. _elements];
        public void StoreElement(XElement element, string friendlyName) => _elements.Add(element);
        public IEnumerable<XElement> Dumped() => _elements;
        public string Dump() => string.Concat(_elements.Select(e => e.ToString()));
    }
}

internal static class DataProtectionTestExtensions
{
    public static IDataProtectionBuilder PersistKeysTo(this IDataProtectionBuilder builder, IXmlRepository repository)
    {
        builder.Services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>(
            o => o.XmlRepository = repository);
        return builder;
    }
}
