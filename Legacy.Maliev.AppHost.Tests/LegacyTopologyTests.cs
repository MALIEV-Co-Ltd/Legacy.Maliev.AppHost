using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyTopologyTests
{
    [Theory]
    [InlineData("PATH", true)]
    [InlineData("SystemRoot", true)]
    [InlineData("DOTNET_ENVIRONMENT", true)]
    [InlineData("ASPIRE_ALLOW_UNSECURED_TRANSPORT", true)]
    [InlineData("ASPNETCORE_URLS", true)]
    [InlineData("Parameters__legacy-postgres-password", true)]
    [InlineData("GITHUB_PERSONAL_ACCESS_TOKEN", false)]
    [InlineData("GOOGLE_ADS_DEVELOPER_TOKEN", false)]
    [InlineData("NUGET_PASSWORD", false)]
    [InlineData("BW_SESSION", false)]
    [InlineData("UNRELATED_MACHINE_VARIABLE", false)]
    public void LocalEnvironmentPolicy_IsFailClosed(string variableName, bool expected)
    {
        Assert.Equal(expected, LocalEnvironmentPolicy.ShouldPreserve(variableName));
    }

    [Fact]
    public void DatabaseNames_MatchTheDormantCloudNativePgContract()
    {
        string[] expected =
        [
            "Country",
            "Currency",
            "Customer",
            "CustomerIdentity",
            "DataProtectionKeys",
            "DataProtectionKeysEmployee",
            "Employee",
            "EmployeeIdentity",
            "Invoice",
            "JobOffers",
            "Material",
            "Message",
            "Order",
            "OrderStatus",
            "Payment",
            "PurchaseOrder",
            "Quotation",
            "QuotationRequest",
            "Receipt",
            "Supplier",
            "Upload"
        ];

        Assert.Equal(expected, LegacyTopology.DatabaseNames);
    }

    [Fact]
    public void CountryConfigurationKeys_MatchTheGitOpsEnvironmentContract()
    {
        string[] expected =
        [
            "ConnectionStrings__CountryDbContext",
            "ConnectionStrings__redis",
            "Jwt__PublicKey",
            "Jwt__Issuer",
            "Jwt__Audience"
        ];

        Assert.Equal(expected, LegacyTopology.CountryConfigurationKeys);
    }

    [Fact]
    public void CreateJwtKeyMaterial_ReturnsAWorkingRsa3072KeyPair()
    {
        var material = LocalJwtKeyMaterial.Create();
        var privatePem = Encoding.UTF8.GetString(Convert.FromBase64String(material.PrivateKeyBase64));
        var publicPem = Encoding.UTF8.GetString(Convert.FromBase64String(material.PublicKeyBase64));
        using var privateKey = RSA.Create();
        using var publicKey = RSA.Create();
        privateKey.ImportFromPem(privatePem);
        publicKey.ImportFromPem(publicPem);
        var payload = "legacy-apphost-contract"u8.ToArray();
        var signature = privateKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.Equal(3072, privateKey.KeySize);
        Assert.Equal(3072, publicKey.KeySize);
        Assert.True(publicKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.StartsWith("-----BEGIN ", privatePem, StringComparison.Ordinal);
        Assert.Contains("PRIVATE KEY", privatePem, StringComparison.Ordinal);
        Assert.StartsWith("-----BEGIN ", publicPem, StringComparison.Ordinal);
        Assert.Contains("PUBLIC KEY", publicPem, StringComparison.Ordinal);
    }
}
