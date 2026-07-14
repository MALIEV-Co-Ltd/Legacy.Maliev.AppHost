namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Defines names shared by the local Aspire host and dormant GitOps resources.</summary>
public static class LegacyTopology
{
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
