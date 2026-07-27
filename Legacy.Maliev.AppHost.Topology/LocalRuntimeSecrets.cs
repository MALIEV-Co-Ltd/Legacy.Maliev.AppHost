using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Contains one ephemeral local service credential and its AuthService hash.</summary>
/// <param name="Secret">Random plaintext credential supplied only to the local caller.</param>
/// <param name="SecretSha256">Lowercase SHA-256 credential hash supplied to AuthService.</param>
public sealed record LocalServiceCredential(string Secret, string SecretSha256)
{
    /// <summary>Creates a credential from an approved Secret Manager value without logging it.</summary>
    public static LocalServiceCredential FromSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException("A GKE validation service credential is missing.");
        }

        var normalizedSecret = secret.Trim();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSecret)));
        return new(normalizedSecret, hash);
    }

    /// <summary>Creates a cryptographically random credential without persisting it.</summary>
    public static LocalServiceCredential Create()
    {
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        return new LocalServiceCredential(secret, hash);
    }
}

/// <summary>Contains an ephemeral certificate used to encrypt the local Web key ring.</summary>
/// <param name="PfxBase64">Base64 PKCS#12 certificate containing its private key.</param>
/// <param name="Password">Random export password.</param>
public sealed record LocalDataProtectionCertificate(string PfxBase64, string Password)
{
    /// <summary>Creates a certificate from an approved Secret Manager value without persisting it.</summary>
    public static LocalDataProtectionCertificate FromSecrets(string pfxBase64, string password)
    {
        if (string.IsNullOrWhiteSpace(pfxBase64) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("The GKE validation data-protection certificate is incomplete.");
        }

        try
        {
            _ = Convert.FromBase64String(pfxBase64.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The GKE validation data-protection certificate is not Base64.", exception);
        }

        return new(pfxBase64.Trim(), password.Trim());
    }

    /// <summary>Creates a short-lived self-signed RSA certificate for one local Aspire run.</summary>
    public static LocalDataProtectionCertificate Create()
    {
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Legacy.Maliev.Web Local Data Protection",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        return new LocalDataProtectionCertificate(
            Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password)),
            password);
    }
}
