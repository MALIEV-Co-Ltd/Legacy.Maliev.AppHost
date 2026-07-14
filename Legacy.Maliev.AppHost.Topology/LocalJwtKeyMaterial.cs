using System.Security.Cryptography;
using System.Text;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Contains ephemeral RSA material for one local Aspire run.</summary>
/// <param name="PrivateKeyBase64">Base64-encoded PKCS#8 private-key PEM.</param>
/// <param name="PublicKeyBase64">Base64-encoded SubjectPublicKeyInfo public-key PEM.</param>
public sealed record LocalJwtKeyMaterial(string PrivateKeyBase64, string PublicKeyBase64)
{
    /// <summary>Creates a new RSA-3072 key pair without persisting it.</summary>
    public static LocalJwtKeyMaterial Create()
    {
        using var rsa = RSA.Create(3072);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        return new LocalJwtKeyMaterial(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(privatePem)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(publicPem)));
    }
}
