namespace Legacy.Maliev.AppHost.Tests;

public sealed class EncryptedSnapshotOrchestrationContractTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void AppHost_ProjectsOnlySnapshotKeyFileReferenceIntoMigrationRunners()
    {
        string source = File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        Assert.Contains("LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE", source, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT_ID", source, StringComparison.Ordinal);
        Assert.Contains("localSnapshotKeyFileRequested", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SnapshotEncryptionKey.Load", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SetEnvironmentVariable(\"LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE\"", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.SetEnvironmentVariable(\"LEGACY_SNAPSHOT_DIRECTORY\"", source,
            StringComparison.Ordinal);
        Assert.Contains("snapshotRunner.WithEnvironment(\"LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE\", localSnapshotKeyFileRequested)", source,
            StringComparison.Ordinal);
        Assert.Contains("snapshotRunner.WithEnvironment(\"LEGACY_SNAPSHOT_DIRECTORY\", localSnapshotDirectoryRequested)", source,
            StringComparison.Ordinal);
        Assert.Contains("snapshotRunner.WithEnvironment(\"LEGACY_SNAPSHOT_ID\", localSnapshotIdRequested)", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationRunner_DecryptsVerifiedSnapshotAndZeroesKeyMaterial()
    {
        string source = File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost.MigrationRunner", "Program.cs")) +
            File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost.MigrationRunner", "PgRestoreProcessTermination.cs")) +
            File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost.MigrationRunner", "PgRestoreRunner.cs"));
        Assert.Contains("SnapshotEncryptionKey.Load", source, StringComparison.Ordinal);
        Assert.Contains("RestoreVerifiedAsync", source, StringComparison.Ordinal);
        Assert.Contains("LEGACY_SNAPSHOT_ID", source, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetArchivePath", source, StringComparison.Ordinal);
        Assert.Contains("ExceptionDispatchInfo.Capture(primaryFailure).Throw", source, StringComparison.Ordinal);
        Assert.Contains("Kill(entireProcessTree: true)", source, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardInput = true", source, StringComparison.Ordinal);
        Assert.Contains("--single-transaction", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(10)", source, StringComparison.Ordinal);
        Assert.Contains("AggregateException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("startInfo.ArgumentList.Add(archivePath)", source, StringComparison.Ordinal);
        string topology = File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost.Topology", "LegacyLocalSnapshot.cs"));
        Assert.DoesNotContain("Path.GetTempPath", topology, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatePlaintextOptions", topology, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(plain)", topology, StringComparison.Ordinal);
        Assert.Contains("workload == \"snapshot-preflight\"", source, StringComparison.Ordinal);
        Assert.Contains("VerifySnapshot(snapshotDirectory)", source, StringComparison.Ordinal);
        Assert.Contains("LegacyLocalSnapshot.Load(snapshotDirectory, key, expectedSnapshotId)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartScript_RequiresKeyFileReferenceAndNeverAcceptsInlineKey()
    {
        foreach (string script in new[]
                 {
                     "start-local-snapshot-aspire.ps1",
                     "start-current-web.ps1",
                     "start-local-review-aspire.ps1",
                 })
        {
            string source = File.ReadAllText(Path.Combine(Root, "scripts", script));
            Assert.Contains("[string]$SnapshotEncryptionKeyFile", source, StringComparison.Ordinal);
            Assert.Contains("LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE", source, StringComparison.Ordinal);
            Assert.Contains("[string]$SnapshotId", source, StringComparison.Ordinal);
            Assert.Contains("LEGACY_LOCAL_SNAPSHOT_ID", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SnapshotEncryptionKey =", source, StringComparison.Ordinal);
            if (script is "start-current-web.ps1" or "start-local-review-aspire.ps1")
            {
                Assert.Contains("AES-256-GCM-chunked-v2", source, StringComparison.Ordinal);
                Assert.Contains("$manifest.Format -ne 'MLVSNP02'", source, StringComparison.Ordinal);
                Assert.DoesNotContain("postgres-custom", source, StringComparison.Ordinal);
                Assert.Contains("$manifest.SnapshotId -ne $SnapshotId", source, StringComparison.Ordinal);
                Assert.Contains("snapshot-preflight", source, StringComparison.Ordinal);
                Assert.Contains("LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE", source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void KeyLoader_UsesExclusiveOpenedHandleIdentityAndOwnerOnlyPermissions()
    {
        string source = File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost.Topology", "LegacyLocalSnapshot.cs")) +
            File.ReadAllText(Path.Combine(Root, "Legacy.Maliev.AppHost.Topology", "SecureSnapshotFile.cs"));
        Assert.Contains("FileShare.None", source, StringComparison.Ordinal);
        Assert.Contains("GetFinalPathNameByHandle", source, StringComparison.Ordinal);
        Assert.Contains("GetEffectiveUserIdNative", source, StringComparison.Ordinal);
        Assert.Contains("statx", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetAccessRules", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceDocumentation_UsesExactTwentyThreeDatabaseExample()
    {
        string source = File.ReadAllText(Path.Combine(Root, "docs", "postgres-migration-evidence.md"));
        const string expected = "ContactRequest,Country,Currency,Customer,CustomerIdentity,DataProtectionKeys,DataProtectionKeysEmployee,Employee,EmployeeIdentity,Invoice,JobOffers,LocationData,Material,Message,Order,OrderStatus,Payment,PurchaseOrder,Quotation,QuotationRequest,Receipt,Supplier,Upload";
        Assert.Contains($"-ExpectedDatabase {expected}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_RequiresExternalSnapshotKeyFileForLocalRestore()
    {
        string source = File.ReadAllText(Path.Combine(Root, "README.md"));
        Assert.Contains("-SnapshotEncryptionKeyFile", source, StringComparison.Ordinal);
        Assert.Contains("never pass key bytes", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not find AppHost repository root.");
    }
}
