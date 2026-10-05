using System.Text.Json;
using Legacy.Maliev.AppHost.MigrationRunner;
using Npgsql;

namespace Legacy.Maliev.AppHost.Tests;

[Collection("AppHostComposition")]
public sealed class MigrationWorkloadDatabaseTests(LocalDeltaBaselineDatabaseTests.DatabaseFixture database)
    : IClassFixture<LocalDeltaBaselineDatabaseTests.DatabaseFixture>
{
    [Theory]
    [MemberData(nameof(MigrationEntryPointTests.Workloads), MemberType = typeof(MigrationEntryPointTests))]
    public async Task FreshOwnedDatabaseMigratesAndRepeatedRunRequiresMatchingSyntheticReceipt(string workload, string connectionName)
    {
        string databaseName = "owned_workload_" + Guid.NewGuid().ToString("N");
        string receiptPath = Path.Combine(Path.GetTempPath(), "owned-schema-receipt-" + Guid.NewGuid().ToString("N") + ".json");
        await using var admin = new NpgsqlConnection(database.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync();
        var target = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName };
        using var environment = new EnvironmentScope();
        environment.Set("LEGACY_SKIP_MIGRATE", null);
        environment.Set("LEGACY_SNAPSHOT_DIRECTORY", null);
        environment.Set("LEGACY_LOCAL_FIXTURES", null);
        environment.Set(SchemaBaselineGate.LocalOverrideEnvironmentVariable, null);
        environment.Set(SchemaBaselineGate.BaselineReceiptEnvironmentVariable, null);
        environment.Set("ConnectionStrings__" + connectionName, target.ConnectionString);
        try
        {
            await MigrationEntryPoint.RunAsync([workload]);
            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();
            long firstHistory;
            await using (var history = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", connection))
                firstHistory = (long)(await history.ExecuteScalarAsync())!;
            Assert.True(firstHistory > 0, "The real workload must apply actual migration history, not only construct a context.");
            await using (var tables = new NpgsqlCommand("""
                SELECT count(*) FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
                """, connection))
                Assert.True((long)(await tables.ExecuteScalarAsync())! > 0);

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationEntryPoint.RunAsync([workload]));
            Assert.Contains("is non-empty", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("No migrations or seed operations were run.", refusal.Message, StringComparison.Ordinal);
            var firstRows = await ReadTableRowCounts(connection);
            string fingerprint = await SchemaBaselineGate.ComputeSchemaFingerprintAsync(connection, CancellationToken.None);
            var receipt = new SchemaBaselineReceipt(workload, databaseName, fingerprint,
                "synthetic-owned-initialization-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt));
            environment.Set(SchemaBaselineGate.BaselineReceiptEnvironmentVariable, receiptPath);
            await MigrationEntryPoint.RunAsync([workload]);
            Assert.Equal(fingerprint, await SchemaBaselineGate.ComputeSchemaFingerprintAsync(connection, CancellationToken.None));
            await using var afterHistory = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", connection);
            Assert.Equal(firstHistory, (long)(await afterHistory.ExecuteScalarAsync())!);
            Assert.Equal(firstRows, await ReadTableRowCounts(connection));
        }
        finally
        {
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<SortedDictionary<string, long>> ReadTableRowCounts(NpgsqlConnection connection)
    {
        var queries = new List<string>();
        await using (var tables = new NpgsqlCommand("""
            SELECT format('SELECT count(*) FROM %I.%I', table_schema, table_name)
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            ORDER BY table_name
            """, connection))
        await using (var reader = await tables.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) queries.Add(reader.GetString(0));
        }
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (string query in queries)
        {
            await using var count = new NpgsqlCommand(query, connection);
            counts.Add(query, (long)(await count.ExecuteScalarAsync())!);
        }
        return counts;
    }
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> original = new(StringComparer.Ordinal);
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