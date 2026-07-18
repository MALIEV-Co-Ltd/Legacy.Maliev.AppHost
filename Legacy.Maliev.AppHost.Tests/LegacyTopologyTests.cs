using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public void Resp3CacheServiceNames_MatchEveryRetainedRedisApi()
    {
        string[] expected =
        [
            "accounting",
            "career",
            "catalog",
            "contact",
            "country",
            "customer",
            "employee",
            "file",
            "order",
            "procurement",
            "quotation",
        ];

        Assert.Equal(expected, LegacyTopology.Resp3CacheServiceNames);
    }

    [Fact]
    public void IntranetPermissions_AreExactAndNeverUseWildcards()
    {
        string[] expected =
        [
            "legacy-auth.customer-identities.create",
            "legacy-auth.employee-identities.create",
            "legacy-auth.employee-self-service",
            "legacy-customer.customers.read",
            "legacy-customer.customers.list",
            "legacy-customer.customers.create",
            "legacy-customer.customers.delete",
            "legacy-employee.employees.read",
            "legacy-employee.employees.list",
            "legacy-employee.employees.create",
            "legacy-employee.employees.delete",
            "legacy-catalog.countries.read",
            "legacy-catalog.currencies.read",
            "legacy-catalog.materials.read",
            "legacy-catalog.materials.create",
            "legacy-catalog.materials.update",
            "legacy-catalog.material-groups.read",
            "legacy-catalog.colors.read",
            "legacy-catalog.surface-finishes.read",
            "legacy-procurement.suppliers.read",
            "legacy-procurement.suppliers.create",
            "legacy-procurement.suppliers.update",
            "legacy-procurement.suppliers.delete",
            "legacy-procurement.supplier-addresses.read",
            "legacy-procurement.supplier-addresses.write",
            "legacy-procurement.purchase-orders.read",
            "legacy-procurement.purchase-orders.create",
            "legacy-procurement.purchase-orders.delete",
            "legacy-procurement.purchase-order-addresses.read",
            "legacy-procurement.order-items.read",
            "legacy-procurement.order-items.write",
            "legacy-procurement.order-items.delete",
            "legacy-procurement.files.read",
            "legacy-procurement.files.write",
            "legacy-procurement.files.delete",
            "legacy.orders.read",
            "legacy.orders.create",
            "legacy.orders.update",
            "legacy.order-catalog.read",
            "legacy.order-files.read",
            "legacy.order-files.write",
            "legacy.order-files.delete",
            "legacy.order-status.read",
            "legacy.order-status.write",
            "legacy.documents.render",
            "legacy-file.uploads.create",
            "legacy-file.uploads.read",
            "legacy-file.uploads.delete",
            "legacy.notifications.send",
            "legacy.accounting.read",
            "legacy.accounting.create",
            "legacy.accounting.update",
            "legacy.accounting.delete",
            "legacy.accounting-files.read",
            "legacy.accounting-files.write",
            "legacy.accounting-files.delete",
            "legacy.quotation-requests.read",
            "legacy.quotation-requests.update",
            "legacy.quotation-files.read",
            "legacy.quotations.read",
            "legacy.quotation-orders.read",
        ];

        Assert.Equal(expected, LegacyTopology.IntranetPermissions);
        Assert.DoesNotContain(LegacyTopology.IntranetPermissions, permission => permission.Contains('*', StringComparison.Ordinal));
        Assert.Equal(LegacyTopology.IntranetPermissions.Count, LegacyTopology.IntranetPermissions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AccountingPermissions_AreExactlyTheSevenServerOwnedDownstreamCapabilities()
    {
        Assert.Equal(
            [
                "legacy.documents.render",
                "legacy-file.uploads.create",
                "legacy-file.uploads.read",
                "legacy-file.uploads.delete",
                "legacy.notifications.send",
                "legacy-customer.customers.read",
                "legacy-employee.signatures.read",
            ],
            LegacyTopology.AccountingPermissions);
        Assert.Equal(7, LegacyTopology.AccountingPermissions.Count);
        Assert.DoesNotContain(
            LegacyTopology.AccountingPermissions,
            permission => permission.Contains('*', StringComparison.Ordinal));
        Assert.Equal(
            LegacyTopology.AccountingPermissions.Count,
            LegacyTopology.AccountingPermissions.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LocalIdentitySeed_IsExplicitlyNonProduction()
    {
        Assert.Equal("local.customer@maliev.test", LegacyTopology.LocalCustomerEmail);
        Assert.Equal("local.employee@maliev.test", LegacyTopology.LocalEmployeeEmail);
        Assert.Equal("local-test-only", LegacyTopology.LocalIdentityPassword);
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

    [Fact]
    public void CreateServiceCredential_ReturnsMatchingRandomSha256Material()
    {
        var first = LocalServiceCredential.Create();
        var second = LocalServiceCredential.Create();
        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(first.Secret)));

        Assert.Equal(64, first.Secret.Length);
        Assert.Equal(expectedHash, first.SecretSha256);
        Assert.NotEqual(first.Secret, second.Secret);
    }

    [Fact]
    public void CreateDataProtectionCertificate_ReturnsAnImportablePrivateKey()
    {
        var material = LocalDataProtectionCertificate.Create();
        using var certificate = X509CertificateLoader.LoadPkcs12(
            Convert.FromBase64String(material.PfxBase64),
            material.Password,
            X509KeyStorageFlags.EphemeralKeySet);

        Assert.True(certificate.HasPrivateKey);
        Assert.Contains("Legacy.Maliev.Web", certificate.Subject, StringComparison.Ordinal);
        Assert.True(certificate.NotAfter > DateTime.UtcNow);
    }
}
