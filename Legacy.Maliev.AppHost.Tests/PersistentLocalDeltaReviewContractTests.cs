using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class PersistentLocalDeltaReviewContractTests
{
    [Fact]
    public void Contract_BindsPersistentTargetToExactMigratedInventory()
    {
        Assert.Equal("legacy-maliev-exact23-postgres-data", PersistentLocalDeltaReviewContract.PostgresVolumeName);
        Assert.Equal("/var/lib/postgresql", PersistentLocalDeltaReviewContract.PostgresVolumeTarget);
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
        Assert.Contains("WithVolume(", source, StringComparison.Ordinal);
        Assert.Contains("PersistentLocalDeltaReviewContract.PostgresVolumeName", source, StringComparison.Ordinal);
        Assert.Contains("PersistentLocalDeltaReviewContract.PostgresVolumeTarget", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WithDataVolume(PersistentLocalDeltaReviewContract.PostgresVolumeName)", source, StringComparison.Ordinal);
        Assert.Contains(".WaitForCompletion(localDeltaApply)", source, StringComparison.Ordinal);
        Assert.Contains("WithEnvironment(\"LEGACY_SKIP_MIGRATE\", \"true\")", source, StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(snapshotRunner, authMigrations)", source, StringComparison.Ordinal);
        Assert.Contains("Auth is runtime-only state", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_CanReviewReconciledPersistentVolumeWithoutReplayingDeltaAuthorization()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));

        Assert.Contains("LEGACY_LOCAL_DELTA_REVIEW", source, StringComparison.Ordinal);
        Assert.Contains(
            "var localPersistentDataMode = localDeltaModeRequested || localDeltaReviewModeRequested;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (localPersistentDataMode)", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (localPersistentDataMode && !ReferenceEquals(snapshotRunner, authMigrations))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var allowExactSnapshotServiceClaims = localSnapshotMode || localPersistentDataMode ? \"true\" : \"false\";",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewLauncher_UsesProtectedPersistentCredentialWithoutDeltaExecution()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string launcher = File.ReadAllText(Path.Combine(root, "scripts", "start-local-delta-review-aspire.ps1"));

        Assert.Contains("LEGACY_LOCAL_DELTA_REVIEW = 'true'", launcher, StringComparison.Ordinal);
        Assert.Contains("Assert-OwnerOnlyFile -Path $PostgresCredentialFile", launcher, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-postgres-username", launcher, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-postgres-password", launcher, StringComparison.Ordinal);
        Assert.Contains("$connection.ContainsKey('ConnectionString')", launcher, StringComparison.Ordinal);
        Assert.Contains("$connection.set_ConnectionString", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("LEGACY_LOCAL_DELTA_CONFIG", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("authorize-delta", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGNING_KEY", launcher, StringComparison.Ordinal);
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
        Assert.Contains("ref: 77c8f3c62ee74bee40c08654fdf457d639ba423f", workflow, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DataMigration", workflow, StringComparison.Ordinal);
    }
}
