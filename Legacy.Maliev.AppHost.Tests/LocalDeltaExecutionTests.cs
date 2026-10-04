using System.Text.Json;
using System.Security.AccessControl;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LocalDeltaExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "legacy-delta-boundary-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("LEGACY_LOCAL_DELTA_CONFIG")]
    [InlineData("ConnectionStrings__legacy-postgres-main")]
    public async Task MissingSetting_RejectsBeforeBaselineOrMigration(string missing)
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalDeltaExecution.RunAsync(
            name => name == missing ? " " : "fixture-only", root,
            _ => { calls.Add("baseline"); return Task.CompletedTask; },
            (_, _, _, _, _) => { calls.Add("migration"); return Task.FromResult(0); }));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task MissingConfig_RejectsBeforeBaseline()
    {
        var config = Path.Combine(root, "missing.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(config, _ => throw new InvalidOperationException("Unexpected baseline call.")));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("existing-file")]
    [InlineData("existing-directory")]
    [InlineData("sibling-prefix")]
    public async Task InvalidTarget_RejectsBeforeBaselineAndPreservesExistingBytes(string kind)
    {
        Directory.CreateDirectory(root);
        var target = kind switch
        {
            "outside" => Path.Combine(Path.GetTempPath(), "unowned-delta-" + Guid.NewGuid().ToString("N")),
            "sibling-prefix" => root + "-sibling" + Path.DirectorySeparatorChar + "connection.txt",
            _ => Path.Combine(root, "existing")
        };
        if (kind == "existing-file") File.WriteAllText(target, "preserve-existing");
        if (kind == "existing-directory") Directory.CreateDirectory(target);
        var config = WriteConfig(target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(config, _ => throw new InvalidOperationException("Unexpected baseline call.")));
        if (kind == "existing-file") Assert.Equal("preserve-existing", File.ReadAllText(target));
        if (kind == "existing-directory") Assert.True(Directory.Exists(target));
        if (kind is "outside" or "sibling-prefix") Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task InvalidJson_RejectsBeforeBaselineOrTargetCreation()
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.json");
        File.WriteAllText(config, "invalid-json");
        LocalDeltaExecution.ProtectFile(config);
        await Assert.ThrowsAnyAsync<JsonException>(() => Run(config, _ => throw new InvalidOperationException("Unexpected baseline call.")));
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public async Task NullTarget_RejectsBeforeBaselineOrTargetCreation()
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.json");
        File.WriteAllText(config, "{\"delta\":{\"targetConnectionFile\":null}}");
        LocalDeltaExecution.ProtectFile(config);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(config, _ => throw new InvalidOperationException("Unexpected baseline call.")));
        Assert.Contains("no target connection file", failure.Message, StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(root));
    }

    [Fact]
    public async Task RejectedBaseline_CreatesNoTargetOrTargetDirectory()
    {
        var target = Path.Combine(root, "new-run", "connection.txt");
        var config = WriteConfig(target);
        var failure = new InvalidOperationException("fixture baseline rejected");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(config, connection =>
        {
            Assert.Equal("fixture-connection", connection);
            Assert.False(File.Exists(target));
            Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
            throw failure;
        }));
        Assert.Same(failure, actual);
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Fact]
    public async Task LocalApply_ForwardsProtectedFixtureAndLocalOnlyEnvironment_ThenDeletesItsConnectionFile()
    {
        var target = Path.Combine(root, "run", "connection.txt");
        var config = WriteConfig(target);
        var calls = new List<string>();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await LocalDeltaExecution.RunAsync(name => EnvironmentFor(name, config), root,
            connection => { Assert.Equal("fixture-connection", connection); calls.Add("baseline"); return Task.CompletedTask; },
            (arguments, actualOutput, actualError, environment, cancellation) =>
            {
                calls.Add("migration");
                Assert.Equal(new[] { "apply-delta-local", "--config", config }, arguments);
                Assert.Same(output, actualOutput);
                Assert.Same(error, actualError);
                Assert.Equal(CancellationToken.None, cancellation);
                Assert.Equal("apphost", environment("LEGACY_MIGRATION_CALLER"));
                Assert.Equal("false", environment("LEGACY_DEPLOY_ENABLED"));
                Assert.Equal("fixture-custom", environment("CUSTOM_INPUT"));
                Assert.Equal("fixture-connection", File.ReadAllText(target));
                LocalDeltaExecution.ValidateOwnerProtected(target);
                return Task.FromResult(19);
            }, output, error);
        Assert.Equal(19, exit);
        Assert.Equal(new[] { "baseline", "migration" }, calls);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(config));
    }

    [Fact]
    public async Task RefusedCreateNew_PreservesForeignFileCreatedAfterPreflight_AndNeverAppliesMigration()
    {
        var target = Path.Combine(root, "run", "connection.txt");
        var config = WriteConfig(target);
        const string sentinel = "foreign-file-created-after-preflight";
        bool migrationCalled = false;
        await Assert.ThrowsAsync<IOException>(() => LocalDeltaExecution.RunAsync(
            name => EnvironmentFor(name, config), root,
            _ =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, sentinel);
                LocalDeltaExecution.ProtectFile(target);
                return Task.CompletedTask;
            },
            (_, _, _, _, _) =>
            {
                migrationCalled = true;
                return Task.FromResult(0);
            }));
        Assert.False(migrationCalled);
        Assert.True(File.Exists(target), "Refused CreateNew must preserve a file this invocation never owned.");
        Assert.Equal(sentinel, File.ReadAllText(target));
        Assert.True(File.Exists(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalApply_PreservesPreexistingParentAndSiblingBytes(bool failMigration)
    {
        var parent = Path.Combine(root, "existing-parent");
        var sibling = Path.Combine(root, "existing-sibling");
        var target = Path.Combine(parent, "connection.txt");
        var config = WriteConfig(target);
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(sibling);
        var parentSentinel = Path.Combine(parent, "keep.txt");
        var siblingSentinel = Path.Combine(sibling, "keep.txt");
        File.WriteAllText(parentSentinel, "existing-parent-bytes");
        File.WriteAllText(siblingSentinel, "existing-sibling-bytes");
        var apply = LocalDeltaExecution.RunAsync(name => EnvironmentFor(name, config), root,
            _ => Task.CompletedTask,
            (_, _, _, _, _) => failMigration
                ? Task.FromException<int>(new InvalidOperationException("fixture apply failed"))
                : Task.FromResult(0));
        if (failMigration) await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
        else Assert.Equal(0, await apply);
        Assert.False(File.Exists(target));
        Assert.Equal("existing-parent-bytes", File.ReadAllText(parentSentinel));
        Assert.Equal("existing-sibling-bytes", File.ReadAllText(siblingSentinel));
        Assert.True(File.Exists(config));
    }

    [Fact]
    public async Task SymlinkAncestorEscape_RejectsBeforeBaselineAndPreservesForeignFixtureBytesAndProtection()
    {
        var protectedRoot = Path.Combine(root, "protected");
        var foreignFixture = Path.Combine(root, "outside-protected-root");
        var redirectedParent = Path.Combine(protectedRoot, "redirected");
        var target = Path.Combine(redirectedParent, "connection.txt");
        var config = WriteConfig(target);
        Directory.CreateDirectory(protectedRoot);
        Directory.CreateDirectory(foreignFixture);
        var sentinel = Path.Combine(foreignFixture, "keep.bin");
        byte[] bytes = [0, 1, 33, 255];
        File.WriteAllBytes(sentinel, bytes);
        var originalProtection = CaptureDirectoryProtection(foreignFixture);
        _ = Directory.CreateSymbolicLink(redirectedParent, foreignFixture);
        try
        {
            Assert.True((File.GetAttributes(redirectedParent) & FileAttributes.ReparsePoint) != 0,
                "The escape fixture must be an actual platform reparse point; setup failure is not behavior RED.");
            Assert.Equal(Path.GetFullPath(foreignFixture), Directory.ResolveLinkTarget(redirectedParent, true)!.FullName);
            bool baselineCalled = false;
            bool migrationCalled = false;
            var failure = await Record.ExceptionAsync(() => LocalDeltaExecution.RunAsync(
                name => EnvironmentFor(name, config), protectedRoot,
                _ => { baselineCalled = true; return Task.CompletedTask; },
                (_, _, _, _, _) => { migrationCalled = true; return Task.FromResult(0); }));
            Assert.False(baselineCalled, "A physically escaping ancestor must be refused before baseline verification.");
            Assert.False(migrationCalled);
            Assert.IsType<InvalidOperationException>(failure);
            Assert.False(File.Exists(Path.Combine(foreignFixture, "connection.txt")));
            Assert.Equal(bytes, File.ReadAllBytes(sentinel));
            Assert.Equal(originalProtection, CaptureDirectoryProtection(foreignFixture));
        }
        finally
        {
            Directory.Delete(redirectedParent);
        }
    }

    private static string CaptureDirectoryProtection(string path)
    {
        if (OperatingSystem.IsWindows())
            return new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        return File.GetUnixFileMode(path).ToString();
    }

    [Fact]
    public async Task FailedLocalApply_DeletesOnlyItsTemporaryConnectionFileAndPropagatesFailure()
    {
        var target = Path.Combine(root, "run", "connection.txt");
        var config = WriteConfig(target);
        var failure = new InvalidOperationException("fixture apply failed");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => LocalDeltaExecution.RunAsync(
            name => EnvironmentFor(name, config), root, _ => Task.CompletedTask,
            (_, _, _, _, _) =>
            {
                Assert.Equal("fixture-connection", File.ReadAllText(target));
                throw failure;
            }));
        Assert.Same(failure, actual);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(config));
    }

    private Task<int> Run(string config, Func<string, Task> baseline) => LocalDeltaExecution.RunAsync(
        name => EnvironmentFor(name, config), root, baseline,
        (_, _, _, _, _) => throw new InvalidOperationException("Unexpected migration call."));

    private static string? EnvironmentFor(string name, string config) => name switch
    {
        "LEGACY_LOCAL_DELTA_CONFIG" => config,
        "ConnectionStrings__legacy-postgres-main" => "fixture-connection",
        "LEGACY_MIGRATION_CALLER" => "untrusted-parent",
        "LEGACY_DEPLOY_ENABLED" => "true",
        "CUSTOM_INPUT" => "fixture-custom",
        _ => null,
    };

    private string WriteConfig(string target)
    {
        Directory.CreateDirectory(root);
        var config = Path.Combine(root, "config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { delta = new { targetConnectionFile = target } }));
        LocalDeltaExecution.ProtectFile(config);
        return config;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
