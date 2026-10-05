namespace Legacy.Maliev.AppHost.Tests;

[Collection("AppHostComposition")]
public sealed class MigrationEntryPointTests
{
    public static TheoryData<string, string> Workloads => new()
    {
        { "auth", "RefreshSessions" }, { "customer-identity", "CustomerIdentity" },
        { "employee-identity", "EmployeeIdentity" }, { "country", "CountryDbContext" },
        { "customer", "CustomerDbContext" }, { "employee", "EmployeeDbContext" },
        { "catalog", "CatalogDbContext" }, { "supplier", "SupplierDbContext" },
        { "purchase-order", "PurchaseOrderDbContext" }, { "file", "FileDbContext" },
        { "order", "OrderDbContext" }, { "order-status", "OrderStatusDbContext" },
        { "quotation", "QuotationDbContext" }, { "quotation-request", "QuotationRequestDbContext" },
        { "career", "CareerDbContext" }, { "contact", "ContactRequestDbContext" },
        { "payment", "PaymentDbContext" }, { "invoice", "InvoiceDbContext" }, { "receipt", "ReceiptDbContext" }
    };

    [Fact]
    public async Task MissingWorkloadRefusesBeforeDatabaseOrSnapshotAccess()
    {
        using var environment = new EntryEnvironment();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationEntryPoint.RunAsync([]));
        Assert.Equal("A migration workload is required.", failure.Message);
    }

    [Theory]
    [MemberData(nameof(Workloads))]
    public async Task EveryWorkloadRequiresItsOwnConnectionBeforeDatabaseAccess(string workload, string connection)
    {
        using var environment = new EntryEnvironment();
        environment.Set("ConnectionStrings__" + connection, null);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationEntryPoint.RunAsync([workload]));
        Assert.Equal($"The {connection} connection string is required.", failure.Message);
    }

    [Fact]
    public async Task SkipWithoutSnapshotReturnsBeforeEvenAnUnusableConnectionIsParsed()
    {
        using var environment = new EntryEnvironment();
        environment.Set("LEGACY_SKIP_MIGRATE", "true");
        environment.Set("ConnectionStrings__CountryDbContext", "not-a-connection-string");
        await MigrationEntryPoint.RunAsync(["country"]);
    }

    [Theory]
    [InlineData("snapshot-preflight", "LEGACY_SNAPSHOT_DIRECTORY is required for snapshot preflight.")]
    [InlineData("snapshot", "Snapshot workloads require LEGACY_SKIP_MIGRATE=true.")]
    [InlineData("unknown", "Unknown migration workload 'unknown'.")]
    public async Task InvalidModeOrWorkloadRefusesBeforeAnyDatabaseAccess(string workload, string expected)
    {
        using var environment = new EntryEnvironment();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationEntryPoint.RunAsync([workload]));
        Assert.Equal(expected, failure.Message);
    }

    [Theory]
    [MemberData(nameof(Workloads))]
    public async Task SkipWithSnapshotRequiresConnectionAndNeverFallsThroughToSeeding(string workload, string connection)
    {
        using var environment = new EntryEnvironment();
        environment.Set("LEGACY_SKIP_MIGRATE", "true");
        environment.Set("LEGACY_SNAPSHOT_DIRECTORY", Path.Combine(Path.GetTempPath(), "unopened-snapshot-" + Guid.NewGuid().ToString("N")));
        environment.Set("ConnectionStrings__" + connection, null);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationEntryPoint.RunAsync([workload]));
        Assert.Equal(workload == "auth"
            ? "Workload 'auth' does not map to a migrated database."
            : $"The {connection} snapshot connection string is required.", failure.Message);
    }

    private sealed class EntryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);

        public EntryEnvironment()
        {
            Set("LEGACY_SKIP_MIGRATE", null);
            Set("LEGACY_SNAPSHOT_DIRECTORY", null);
            Set("LEGACY_LOCAL_FIXTURES", null);
        }

        public void Set(string name, string? value)
        {
            if (!original.ContainsKey(name)) original.Add(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var entry in original) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
