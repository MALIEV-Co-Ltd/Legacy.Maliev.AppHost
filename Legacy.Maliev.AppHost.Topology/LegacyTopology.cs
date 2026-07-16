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

    /// <summary>Gets the exact permissions required by currently migrated Intranet workflows.</summary>
    public static IReadOnlyList<string> IntranetPermissions { get; } =
    [
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
    ];
}
