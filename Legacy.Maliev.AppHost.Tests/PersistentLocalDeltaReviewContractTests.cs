using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class PersistentLocalDeltaReviewContractTests
{
    [Fact]
    public void Contract_BindsPersistentTargetToExactMigratedInventory()
    {
        Assert.Equal("legacy-maliev-exact23-postgres-data", PersistentLocalDeltaReviewContract.PostgresVolumeName);
        Assert.Equal(LegacySnapshotReviewContract.MigratedDatabases, PersistentLocalDeltaReviewContract.Databases);
        Assert.DoesNotContain("Hangfire", PersistentLocalDeltaReviewContract.Databases);
        Assert.DoesNotContain("Log", PersistentLocalDeltaReviewContract.Databases);
    }

    [Fact]
    public void Contract_ExposesOnlyLocalApplyCommand()
    {
        Assert.Equal("apply-delta-local", PersistentLocalDeltaReviewContract.ApplyCommand);
    }

    [Fact]
    public void WorkspaceResolution_PrefersCanonicalRepositoriesOverSiblingWorktrees()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        int canonical = props.IndexOf("$(MSBuildThisFileDirectory)..\\..\\Legacy.Maliev.CountryService", StringComparison.Ordinal);
        int sibling = props.IndexOf("$(MSBuildThisFileDirectory)..\\Legacy.Maliev.CountryService", StringComparison.Ordinal);

        Assert.True(canonical >= 0);
        Assert.True(sibling > canonical);
    }

    [Fact]
    public void AppHost_RequestsPersistentVolumeAndWaitsForGuardedLocalDelta()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));

        Assert.Contains("LEGACY_LOCAL_DELTA", source, StringComparison.Ordinal);
        Assert.Contains("legacy-local-delta-apply", source, StringComparison.Ordinal);
        Assert.Contains("WithDataVolume(PersistentLocalDeltaReviewContract.PostgresVolumeName)", source, StringComparison.Ordinal);
        Assert.Contains(".WaitForCompletion(localDeltaApply)", source, StringComparison.Ordinal);
        Assert.Contains("WithEnvironment(\"LEGACY_SKIP_MIGRATE\", \"true\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_HardcodesLocalApplyAndNeverOffersProductionOrSigningCommands()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string runner = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost.LocalDeltaRunner", "Program.cs"));
        Assert.Contains("[\"apply-delta-local\", \"--config\", configPath]", runner, StringComparison.Ordinal);
        Assert.Contains("\"LEGACY_MIGRATION_CALLER\" => \"apphost\"", runner, StringComparison.Ordinal);
        Assert.Contains("\"LEGACY_DEPLOY_ENABLED\" => \"false\"", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("apply-delta-production", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("plan-delta", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("authorize-delta", runner, StringComparison.Ordinal);
        Assert.Contains("baseline_evidence_sha256 ~ '^[0-9a-f]{64}$'", runner, StringComparison.Ordinal);
        Assert.Contains("source_cutoff_utc <= reconciled_at_utc", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_PinsGuardedDeltaProducerUsedByTheRunner()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "_build-and-test.yml"));

        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.DataMigration", workflow, StringComparison.Ordinal);
        Assert.Contains("ref: d4beb4b432da3dd64c024466022dd1c451f81650", workflow, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DataMigration", workflow, StringComparison.Ordinal);
    }
}
