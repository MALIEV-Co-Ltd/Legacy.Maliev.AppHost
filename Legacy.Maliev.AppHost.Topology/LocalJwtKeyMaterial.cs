using System.Security.Cryptography;
using System.Text;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Contains ephemeral RSA material for one local Aspire run.</summary>
/// <param name="PrivateKeyBase64">Base64-encoded PKCS#8 private-key PEM.</param>
/// <param name="PublicKeyBase64">Base64-encoded SubjectPublicKeyInfo public-key PEM.</param>
public sealed record LocalJwtKeyMaterial(string PrivateKeyBase64, string PublicKeyBase64)
{
    /// <summary>Gets the PKCS#8 private key PEM expected by AuthService.</summary>
    public string PrivateKeyPem => Encoding.UTF8.GetString(Convert.FromBase64String(PrivateKeyBase64));

    /// <summary>Gets the SubjectPublicKeyInfo public key PEM.</summary>
    public string PublicKeyPem => Encoding.UTF8.GetString(Convert.FromBase64String(PublicKeyBase64));

    /// <summary>Creates key material from the value-free Secret Manager contract.</summary>
    public static LocalJwtKeyMaterial FromSecrets(string privateKeyPem, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPem) || string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            throw new InvalidOperationException("The GKE validation JWT key material is incomplete.");
        }

        var normalizedPrivateKeyPem = privateKeyPem.Trim();
        var normalizedPublicKeyBase64 = publicKeyBase64.Trim();
        if (normalizedPublicKeyBase64.Contains("BEGIN", StringComparison.Ordinal))
        {
            normalizedPublicKeyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalizedPublicKeyBase64));
        }

        try
        {
            var publicKeyPem = Encoding.UTF8.GetString(Convert.FromBase64String(normalizedPublicKeyBase64));
            using var privateKey = RSA.Create();
            using var publicKey = RSA.Create();
            privateKey.ImportFromPem(normalizedPrivateKeyPem);
            publicKey.ImportFromPem(publicKeyPem);
            if (privateKey.KeySize != publicKey.KeySize)
            {
                throw new CryptographicException("JWT key sizes do not match.");
            }

            var proof = "legacy-gke-validation"u8.ToArray();
            var signature = privateKey.SignData(
                proof,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            if (!publicKey.VerifyData(
                    proof,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                throw new CryptographicException("JWT public and private keys do not match.");
            }
        }
        catch (Exception exception) when (
            exception is FormatException
            or ArgumentException
            or CryptographicException)
        {
            throw new InvalidOperationException("The GKE validation JWT key material is invalid.", exception);
        }

        return new(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(normalizedPrivateKeyPem)),
            normalizedPublicKeyBase64);
    }

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
