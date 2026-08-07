using System.Text.Json;
using Legacy.Maliev.AppHost.MigrationRunner;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class SchemaBaselineGateTests
{
    [Fact]
    public void Fingerprint_IsDeterministicAndOrderIndependent()
    {
        var columns = new[]
        {
            new SchemaColumn("public", "orders", "id", 1, "integer", "int4", "NO", "", "32", "0", "", ""),
            new SchemaColumn("public", "orders", "created_at", 2, "timestamp", "timestamp", "NO", "", "", "", "6", "now()"),
        };

        var expected = SchemaBaselineGate.ComputeSchemaFingerprint(columns);

        Assert.Equal(expected, SchemaBaselineGate.ComputeSchemaFingerprint(columns.Reverse()));
        Assert.Equal(64, expected.Length);
        Assert.All(expected, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void Fingerprint_ChangesWhenAColumnDefinitionChanges()
    {
        var original = new SchemaColumn(
            "public",
            "orders",
            "id",
            1,
            "integer",
            "int4",
            "NO",
            "",
            "32",
            "0",
            "",
            "");
        var changed = original with { DataType = "bigint", UdtName = "int8" };

        Assert.NotEqual(
            SchemaBaselineGate.ComputeSchemaFingerprint(new[] { original }),
            SchemaBaselineGate.ComputeSchemaFingerprint(new[] { changed }));
    }

    [Fact]
    public void ReceiptValidation_RequiresTheExpectedWorkloadDatabaseAndFingerprint()
    {
        var receipt = new SchemaBaselineReceipt(
            "customer",
            "Customer",
            new string('a', 64),
            "gcs://maliev.com/database/full/2026-08-07/backup.dump",
            DateTimeOffset.Parse("2026-08-07T00:00:00Z"));

        SchemaBaselineGate.ValidateReceipt(receipt, "customer", "Customer", new string('a', 64));

        var wrongWorkload = Assert.Throws<InvalidOperationException>(() =>
            SchemaBaselineGate.ValidateReceipt(receipt, "order", "Customer", new string('a', 64)));
        Assert.Contains("workload", wrongWorkload.Message, StringComparison.OrdinalIgnoreCase);

        var wrongDatabase = Assert.Throws<InvalidOperationException>(() =>
            SchemaBaselineGate.ValidateReceipt(receipt, "customer", "Order", new string('a', 64)));
        Assert.Contains("database", wrongDatabase.Message, StringComparison.OrdinalIgnoreCase);

        var wrongFingerprint = Assert.Throws<InvalidOperationException>(() =>
            SchemaBaselineGate.ValidateReceipt(receipt, "customer", "Customer", new string('b', 64)));
        Assert.Contains("schema", wrongFingerprint.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReceiptReader_RejectsUnknownFieldsAndMalformedFingerprints()
    {
        var directory = Directory.CreateTempSubdirectory("legacy-schema-receipt-");
        try
        {
            var unknownFieldPath = Path.Combine(directory.FullName, "unknown.json");
            File.WriteAllText(
                unknownFieldPath,
                """
                {
                  "workload": "customer",
                  "database": "Customer",
                  "schemaFingerprint": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sourceBackupId": "backup-2026-08-07",
                  "capturedAtUtc": "2026-08-07T00:00:00Z",
                  "unexpected": true
                }
                """);

            var unknownField = Assert.Throws<InvalidOperationException>(() =>
                SchemaBaselineGate.ReadReceipt(unknownFieldPath));
            Assert.Contains("not valid JSON", unknownField.Message, StringComparison.OrdinalIgnoreCase);

            var malformedPath = Path.Combine(directory.FullName, "malformed.json");
            File.WriteAllText(
                malformedPath,
                JsonSerializer.Serialize(
                    new SchemaBaselineReceipt(
                        "customer",
                        "Customer",
                        "not-a-fingerprint",
                        "backup-2026-08-07",
                        DateTimeOffset.Parse("2026-08-07T00:00:00Z"))));

            var malformed = Assert.Throws<InvalidOperationException>(() =>
                SchemaBaselineGate.ReadReceipt(malformedPath));
            Assert.Contains("SHA-256", malformed.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void LocalOverride_IsExplicitAndCaseInsensitive()
    {
        Assert.True(SchemaBaselineGate.IsLocalOverride("true"));
        Assert.True(SchemaBaselineGate.IsLocalOverride("TRUE"));
        Assert.False(SchemaBaselineGate.IsLocalOverride("false"));
        Assert.False(SchemaBaselineGate.IsLocalOverride(null));
    }

    [Fact]
    public void MigrationRunner_InvokesTheGateBeforeApplyingMigrations()
    {
        var root = FindRepositoryRoot();
        var runner = File.ReadAllText(Path.Combine(
            root,
            "Legacy.Maliev.AppHost.MigrationRunner",
            "Program.cs"));

        var gateIndex = runner.IndexOf("EnsureSafeToMigrateAsync", StringComparison.Ordinal);
        var migrationIndex = runner.IndexOf("await MigrateAsync(workload", StringComparison.Ordinal);

        Assert.True(gateIndex >= 0, "Expected the schema baseline gate in the migration runner.");
        Assert.True(migrationIndex > gateIndex, "The baseline gate must run before MigrateAsync.");
    }

    [Fact]
    public void AppHost_OnlyEnablesNonEmptyMigrationOverrideForLocalOrAuthInfrastructure()
    {
        var root = FindRepositoryRoot();
        var appHost = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));

        Assert.Contains(
            ".WithEnvironment(\"LEGACY_LOCAL_ALLOW_NONEMPTY_MIGRATE\", !gkeValidationMode && !localSnapshotMode ? \"true\" : \"false\")",
            appHost,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"LEGACY_LOCAL_ALLOW_NONEMPTY_MIGRATE\", gkeValidationMode ? \"false\" : \"true\")",
            ExtractResource(appHost, "var authMigrations", "var customerIdentityMigrations"),
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"LEGACY_LOCAL_ALLOW_NONEMPTY_MIGRATE\", \"false\")",
            ExtractResource(appHost, "IResourceBuilder<ProjectResource> AddSnapshotMigration", "var auth ="),
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ExtractResource(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected resource marker '{startMarker}'.");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected resource terminator '{endMarker}'.");
        return source[start..end];
    }
}
