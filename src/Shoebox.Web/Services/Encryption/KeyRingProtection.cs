using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Shoebox.Web.Services.Encryption;

/// <summary>
/// Encrypts the ASP.NET data-protection key ring under the master key.
///
/// Those keys sign the unlock and admin cookies, so left in the clear on the data volume they
/// are a third bearer credential sitting next to the photos: whoever has them can mint a valid
/// unlock cookie for any box. They have to persist across restarts, so they can't just live in
/// memory — hence the same treatment as everything else on the volume.
/// </summary>
public sealed class MasterKeyXmlEncryptor(MasterKey masterKey) : IXmlEncryptor
{
    private static ReadOnlySpan<byte> AssociatedData => "shoebox-keyring-v1"u8;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var wrapped = masterKey.Wrap(plaintext, AssociatedData);

        var element = new XElement("encryptedKey",
            new XComment(" This key is encrypted with the Shoebox master key. "),
            new XElement("value", Convert.ToBase64String(wrapped)));

        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }

    internal static byte[] Unwrap(MasterKey masterKey, string base64) =>
        masterKey.Unwrap(Convert.FromBase64String(base64), AssociatedData);
}

/// <summary>
/// The other half of <see cref="MasterKeyXmlEncryptor"/>. Data protection activates this by
/// type name, passing the service provider, so the master key is resolved at decrypt time.
/// </summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var masterKey = services.GetRequiredService<MasterKey>();
        var value = encryptedElement.Element("value")?.Value
            ?? throw new InvalidOperationException("Encrypted key-ring element has no value.");

        var plaintext = MasterKeyXmlEncryptor.Unwrap(masterKey, value);
        return XElement.Parse(Encoding.UTF8.GetString(plaintext));
    }
}
