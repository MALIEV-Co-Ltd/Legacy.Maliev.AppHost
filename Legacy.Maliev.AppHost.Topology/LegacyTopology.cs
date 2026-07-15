namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Defines names shared by the local Aspire host and dormant GitOps resources.</summary>
public static class LegacyTopology
{
    /// <summary>Gets the issuer shared by all local legacy JWT producers and consumers.</summary>
    public const string JwtIssuer = "https://legacy-auth.localhost";

    /// <summary>Gets the audience shared by all local legacy JWT producers and consumers.</summary>
    public const string JwtAudience = "maliev-legacy";

    /// <summary>Gets the non-production key identifier for the ephemeral local signing key.</summary>
    public const string JwtKeyId = "legacy-local-ephemeral";

    /// <summary>Gets the synthetic customer identity used only by the disposable local stack.</summary>
    public const string LocalCustomerEmail = "local.customer@maliev.test";

    /// <summary>Gets the synthetic employee identity used only by the disposable local stack.</summary>
    public const string LocalEmployeeEmail = "local.employee@maliev.test";

    /// <summary>Gets the clearly non-production password used by local integration verification.</summary>
    public const string LocalIdentityPassword = "local-test-only";

    /// <summary>Gets the legacy PostgreSQL database names without schema renaming.</summary>
    public static IReadOnlyList<string> DatabaseNames { get; } =
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

    /// <summary>Gets configuration keys required by the migrated Country service.</summary>
    public static IReadOnlyList<string> CountryConfigurationKeys { get; } =
    [
        "ConnectionStrings__CountryDbContext",
        "ConnectionStrings__redis",
        "Jwt__PublicKey",
        "Jwt__Issuer",
        "Jwt__Audience"
    ];
}
