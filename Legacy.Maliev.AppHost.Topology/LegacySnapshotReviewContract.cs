namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Freezes the exact local review inventory for an authenticated production-derived snapshot.</summary>
public static class LegacySnapshotReviewContract
{
    /// <summary>Gets the terminal migration and restore jobs that must complete successfully.</summary>
    public static IReadOnlyList<string> TerminalJobs { get; } = Array.AsReadOnly(new[]
    {
        "legacy-country-migrations",
        "legacy-auth-migrations",
        "legacy-customer-identity-migrations",
        "legacy-employee-identity-migrations",
        "legacy-customer-migrations",
        "legacy-employee-migrations",
        "legacy-catalog-migrations",
        "legacy-supplier-migrations",
        "legacy-purchase-order-migrations",
        "legacy-file-migrations",
        "legacy-order-migrations",
        "legacy-order-status-migrations",
        "legacy-quotation-migrations",
        "legacy-quotation-request-migrations",
        "legacy-career-migrations",
        "legacy-contact-migrations",
        "legacy-payment-migrations",
        "legacy-invoice-migrations",
        "legacy-receipt-migrations",
        "legacy-contact-request-snapshot",
        "legacy-currency-snapshot",
        "legacy-data-protection-keys-snapshot",
        "legacy-data-protection-keys-employee-snapshot",
        "legacy-location-data-snapshot",
        "legacy-log-archive-snapshot",
    });

    /// <summary>Gets the services that must become healthy and pass a read-only probe.</summary>
    public static IReadOnlyList<string> Services { get; } = Array.AsReadOnly(new[]
    {
        "legacy-maliev-country-service",
        "legacy-maliev-document-service",
        "legacy-maliev-auth-service",
        "legacy-maliev-customer-service",
        "legacy-maliev-employee-service",
        "legacy-maliev-catalog-service",
        "legacy-maliev-procurement-service",
        "legacy-maliev-file-service",
        "legacy-maliev-order-service",
        "legacy-maliev-quotation-service",
        "legacy-maliev-notification-service",
        "legacy-maliev-web",
        "legacy-maliev-intranet-bff",
        "legacy-maliev-career-service",
        "legacy-maliev-contact-service",
        "legacy-maliev-accounting-service",
    });

    /// <summary>Gets the reviewed repositories whose clean protected-main commits form the runtime baseline.</summary>
    public static IReadOnlyList<string> Repositories { get; } = Array.AsReadOnly(new[]
    {
        "Legacy.Maliev.AppHost",
        "Legacy.Maliev.AccountingService",
        "Legacy.Maliev.AuthService",
        "Legacy.Maliev.CareerService",
        "Legacy.Maliev.CatalogService",
        "Legacy.Maliev.CompatibilityContracts",
        "Legacy.Maliev.ContactService",
        "Legacy.Maliev.CountryService",
        "Legacy.Maliev.CustomerService",
        "Legacy.Maliev.DocumentService",
        "Legacy.Maliev.EmployeeService",
        "Legacy.Maliev.FileService",
        "Legacy.Maliev.Intranet",
        "Legacy.Maliev.NotificationService",
        "Legacy.Maliev.OrderService",
        "Legacy.Maliev.ProcurementService",
        "Legacy.Maliev.QuotationService",
        "Legacy.Maliev.ServiceDefaults",
        "Legacy.Maliev.Web",
    });

    /// <summary>Gets the databases migrated from SQL Server. Auth is a separate runtime database.</summary>
    public static IReadOnlyList<string> MigratedDatabases { get; } = Array.AsReadOnly(new[]
    {
        "ContactRequest",
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
        "LocationData",
        "Log",
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
        "Upload",
    });

    /// <summary>Gets the authenticated, read-only query observations required before terminal success.</summary>
    public static IReadOnlyList<string> AuthenticatedReadQueries { get; } = Array.AsReadOnly(new[]
    {
        "auth-session-current",
        "document-receipt-read",
        "customer-list",
        "employee-list",
        "catalog-material-list",
        "procurement-supplier-list",
        "file-list",
        "order-list",
        "quotation-list",
        "intranet-customer-list",
        "accounting-invoice-list",
    });
}
