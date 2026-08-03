using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using RediensIAM.Services;

namespace RediensIAM.Config;

/// <summary>
/// Encryption at rest for the DataProtection key ring (R-15, defence in depth beside cache TLS).
///
/// <para>
/// The ring is persisted to Dragonfly, and out of the box it is persisted <b>in the clear</b>:
/// ASP.NET Core logs one warning and carries on. Anyone who can read that key can mint session
/// cookies and forge every DataProtection payload the deployment has. TLS moves the plaintext off
/// the wire; it does nothing about the plaintext sitting in the cache, in a memory dump, or in a
/// snapshot. This closes that half.
/// </para>
///
/// <para>
/// The protector is the same HKDF root the rest of the deployment already carries
/// (<see cref="AppConfig.DataProtectionKey"/>), under its own purpose string, and the ciphertext
/// goes through <see cref="TotpEncryption"/> — so root rotation applies here for free: every
/// configured root can decrypt, only the active one encrypts.
/// </para>
/// </summary>
public static class KeyRingProtection
{
    /// <summary>
    /// Encrypts every key the ring writes, and refuses to read one that is not encrypted.
    ///
    /// <para>
    /// Both halves are registered with <c>PostConfigure</c> rather than <c>Configure</c>, so this
    /// call may appear anywhere in the <see cref="IDataProtectionBuilder"/> chain. Ordering is the
    /// trap here: an encryptor registered after the repository has already been read protects
    /// nothing, and a decorator registered before <c>PersistKeysTo…</c> would wrap a null
    /// repository. <c>PostConfigure</c> runs after every <c>Configure</c>, so neither can happen.
    /// </para>
    /// </summary>
    public static IDataProtectionBuilder ProtectKeysWithRootKey(this IDataProtectionBuilder builder, AppConfig appConfig)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(appConfig);

        builder.Services.PostConfigure<KeyManagementOptions>(options =>
        {
            options.XmlEncryptor = new RootKeyXmlEncryptor(appConfig.DataProtectionKey);
            options.XmlRepository = new EncryptedOnlyXmlRepository(
                options.XmlRepository ?? throw new InvalidOperationException(
                    "ProtectKeysWithRootKey needs a key repository — call PersistKeysTo… on the same " +
                    "DataProtection builder. Without one the ring lands in the default file-system " +
                    "location, which is ephemeral in a container and unprotected by this decorator."));
        });
        return builder;
    }
}

/// <summary>Wraps each key element's secret in AES-GCM under the deployment's derived key.</summary>
public sealed class RootKeyXmlEncryptor(KeyRing ring) : IXmlEncryptor
{
    /// <summary>Name of the element this encryptor writes. Also what <see cref="EncryptedOnlyXmlRepository"/> looks for.</summary>
    public const string ElementName = "rediensiamEncryptedKey";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var ciphertext = TotpEncryption.EncryptString(ring, plaintextElement.ToString(SaveOptions.DisableFormatting));
        return new EncryptedXmlInfo(new XElement(ElementName, ciphertext), typeof(RootKeyXmlDecryptor));
    }
}

/// <summary>
/// Counterpart of <see cref="RootKeyXmlEncryptor"/>.
///
/// <para>
/// <b>The constructor signature is load-bearing.</b> This type is not resolved from DI — its name
/// is recorded in the stored XML and DataProtection's activator instantiates it on first key-ring
/// read, accepting only a parameterless constructor or one taking <see cref="IServiceProvider"/>.
/// A constructor taking <see cref="AppConfig"/> or <see cref="KeyRing"/> directly compiles, passes
/// a unit test that news it up by hand, and then fails on the deploy <i>after</i> the one that
/// wrote the keys — at which point every session is unreadable. That is why the round-trip test
/// restarts the host instead of reusing it.
/// </para>
/// </summary>
public sealed class RootKeyXmlDecryptor : IXmlDecryptor
{
    private readonly KeyRing _ring;

    public RootKeyXmlDecryptor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ring = ((AppConfig)services.GetService(typeof(AppConfig))!
            ?? throw new InvalidOperationException(
                "AppConfig is not registered, so the DataProtection key ring cannot be decrypted.")).DataProtectionKey;
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        return XElement.Parse(TotpEncryption.DecryptString(_ring, encryptedElement.Value));
    }
}

/// <summary>
/// Refuses to hand DataProtection a key element that was stored unencrypted.
///
/// <para>
/// Without this the protection is one-way and buys much less than it looks like it does: an
/// attacker who can write to the cache cannot read the existing ring, but he can <i>append</i> a
/// plaintext key of his own, which DataProtection will happily adopt and use to mint cookies.
/// Rejecting on read is what makes "the ring is encrypted" an invariant rather than a habit.
/// </para>
///
/// <para>
/// It throws rather than skipping. A skipped key is a silent fallback to a smaller ring — exactly
/// the failure mode this whole file exists to prevent — and the operator sees nothing.
/// </para>
/// </summary>
public sealed class EncryptedOnlyXmlRepository(IXmlRepository inner) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = inner.GetAllElements();
        foreach (var element in elements)
        {
            // Only <key> elements carry secrets. <revocation> elements are public facts about
            // which keys are dead and are correctly stored in the clear.
            if (element.Name.LocalName != "key") continue;
            if (element.Descendants(RootKeyXmlEncryptor.ElementName).Any()) continue;

            throw new InvalidOperationException(
                $"DataProtection key '{element.Attribute("id")?.Value ?? "?"}' is stored unencrypted. " +
                "Refusing to use it: an unprotected key in a shared cache can be read — or planted — " +
                "by anyone with access to that cache, and either way it mints session cookies. " +
                "If this is a ring written before key-ring protection was enabled, delete it " +
                "(DEL rediensiam:dataprotection:keys) and accept the one-time session loss.");
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName) => inner.StoreElement(element, friendlyName);
}
