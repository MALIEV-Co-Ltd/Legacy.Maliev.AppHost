namespace Legacy.Maliev.AppHost.Tests;

public sealed class AppHostStartupGuardTests
{
    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void ConflictingModes_RejectBeforeAnySnapshotFileRequirement(bool gke, bool snapshot, bool delta, bool review)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AppHostStartupGuard.Validate(
            gke, snapshot, delta, review, false, null, null, null, null));
        Assert.Contains("mutually exclusive", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AllowedModes_DoNotRequireSnapshotInputs(bool gke, bool delta, bool review) =>
        AppHostStartupGuard.Validate(gke, false, delta, review, false, null, null, null, delta ? "prepared-config.json" : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SnapshotWithoutDirectory_RejectsWithRequiredSetting(string? directory)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AppHostStartupGuard.Validate(
            false, true, false, false, false, directory, null, null, null));
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT_DIR", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("missing")]
    public void SnapshotWithoutExistingKey_RejectsBeforeIdentity(string? key)
    {
        if (key == "missing") key = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.key");
        var failure = Assert.Throws<InvalidOperationException>(() => AppHostStartupGuard.Validate(
            false, true, false, false, false, "prepared-directory", key, null, null));
        Assert.Contains("LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotWithoutIdentity_RejectsAfterExistingKeyCheck()
    {
        WithFixtureFile(key =>
        {
            var failure = Assert.Throws<InvalidOperationException>(() => AppHostStartupGuard.Validate(
                false, true, false, false, false, "prepared-directory", key, " ", null));
            Assert.Contains("LEGACY_LOCAL_SNAPSHOT_ID", failure.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteSnapshotInputs_AcceptWithOrWithoutFixtures(bool fixtures) => WithFixtureFile(key =>
        AppHostStartupGuard.Validate(false, true, false, false, fixtures, "prepared-directory", key, "fixture-snapshot", null));

    [Fact]
    public void FixturesWithoutSnapshot_RejectsBeforeAppConstruction()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AppHostStartupGuard.Validate(
            false, false, false, false, true, null, null, null, null));
        Assert.Contains("LEGACY_LOCAL_FIXTURES requires LEGACY_LOCAL_SNAPSHOT", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void LocalDeltaWithoutConfig_RejectsBeforeAppConstruction(string? config)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => AppHostStartupGuard.Validate(
            false, false, true, false, false, null, null, null, config));
        Assert.Contains("LEGACY_LOCAL_DELTA_CONFIG", failure.Message, StringComparison.Ordinal);
    }

    private static void WithFixtureFile(Action<string> assertion)
    {
        var path = Path.GetTempFileName();
        try { assertion(path); }
        finally { File.Delete(path); }
    }
}
