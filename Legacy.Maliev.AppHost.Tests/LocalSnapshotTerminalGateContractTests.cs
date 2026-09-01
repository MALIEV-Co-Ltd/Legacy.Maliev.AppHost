using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LocalSnapshotTerminalGateContractTests
{
    [Fact]
    public void Contract_FreezesExactRuntimeInventory()
    {
        Assert.Equal(25, LegacySnapshotReviewContract.TerminalJobs.Count);
        Assert.Equal(16, LegacySnapshotReviewContract.Services.Count);
        Assert.Equal(19, LegacySnapshotReviewContract.Repositories.Count);
        Assert.Equal(24, LegacySnapshotReviewContract.MigratedDatabases.Count);
        Assert.DoesNotContain("Hangfire", LegacySnapshotReviewContract.MigratedDatabases);
        Assert.Contains("legacy-auth-migrations", LegacySnapshotReviewContract.TerminalJobs);
        Assert.Contains("legacy-log-archive-snapshot", LegacySnapshotReviewContract.TerminalJobs);
    }

    [Fact]
    public void Verifier_IsFailClosedAndReadOnly()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "scripts", "verify-local-snapshot-stack.ps1"));

        Assert.Contains("snapshot-preflight", source, StringComparison.Ordinal);
        Assert.Contains("verify-postgres-migration-evidence.ps1", source, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_FIXTURES", source, StringComparison.Ordinal);
        Assert.Contains("'false'", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedSourceCommitSha", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedRepositoryBaselineSha256", source, StringComparison.Ordinal);
        Assert.Contains("LegacySnapshotReviewContract", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-Method Post", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Put", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Method Delete", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local.employee@maliev.test", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local.customer@maliev.test", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TerminalEvidenceValidator_AcceptsRedactedPassedEvidence()
    {
        using var fixture = TerminalEvidenceFixture.Create();
        ProcessResult result = await RunValidatorAsync(fixture);
        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}{Environment.NewLine}stderr: {result.Error}");
    }

    [Theory]
    [InlineData("failed-status")]
    [InlineData("wrong-source")]
    [InlineData("wrong-snapshot-format")]
    [InlineData("hangfire-database")]
    [InlineData("missing-job")]
    [InlineData("unhealthy-service")]
    [InlineData("fixture-enabled")]
    [InlineData("mutating-probe")]
    [InlineData("pii-field")]
    [InlineData("stale")]
    [InlineData("dirty-repository")]
    [InlineData("ahead-repository")]
    [InlineData("wrong-repository-baseline")]
    [InlineData("wrong-migration-evidence")]
    [InlineData("count-field")]
    public async Task TerminalEvidenceValidator_RejectsUnsafeOrIncompleteEvidence(string mutation)
    {
        using var fixture = TerminalEvidenceFixture.Create(mutation);
        ProcessResult result = await RunValidatorAsync(fixture);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void Documentation_RequiresSignedBindingAndNonMutatingReview()
    {
        string root = FindRepositoryRoot();
        string docs = File.ReadAllText(Path.Combine(root, "docs", "local-snapshot-terminal-validation.md"));

        Assert.Contains("authoritative source commit", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("25 terminal jobs", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24 migrated databases plus Auth", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-mutating", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no PII", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hangfire", docs, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunValidatorAsync(TerminalEvidenceFixture fixture)
    {
        string root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            "-NoLogo", "-NoProfile", "-File",
            Path.Combine(root, "scripts", "test-local-snapshot-verification-evidence.ps1"),
            "-EvidencePath", fixture.Path,
            "-ExpectedAppHostCommit", fixture.AppHostCommit,
            "-ExpectedSourceCommitSha", fixture.SourceCommit,
            "-ExpectedSnapshotId", fixture.SnapshotId,
            "-ExpectedManifestDigestSha256", fixture.ManifestDigest,
            "-ExpectedMigrationEvidencePayloadSha256", fixture.MigrationEvidenceDigest,
            "-ExpectedApprovedBaselineSha256", fixture.ApprovedBaselineDigest,
            "-ExpectedRepositoryBaselineSha256", fixture.RepositoryBaselineDigest,
            "-MaximumAgeMinutes", "30",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, output, error);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TerminalEvidenceFixture : IDisposable
    {
        private TerminalEvidenceFixture(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }
        public string Path { get; }
        public string AppHostCommit { get; } = new('a', 40);
        public string SourceCommit { get; } = new('b', 40);
        public string SnapshotId { get; } = "exact24-review-20260901";
        public string ManifestDigest { get; } = new('1', 64);
        public string MigrationEvidenceDigest { get; } = new('d', 64);
        public string ApprovedBaselineDigest { get; } = new('e', 64);
        public string RepositoryBaselineDigest { get; } = new('f', 64);

        public static TerminalEvidenceFixture Create(string? mutation = null)
        {
            string directory = System.IO.Directory.CreateTempSubdirectory("legacy-local-snapshot-evidence-").FullName;
            string path = System.IO.Path.Combine(directory, "evidence.json");
            var fixture = new TerminalEvidenceFixture(directory, path);
            DateTimeOffset completed = mutation == "stale" ? DateTimeOffset.UtcNow.AddHours(-2) : DateTimeOffset.UtcNow;
            string[] databases = [.. LegacySnapshotReviewContract.MigratedDatabases, "Auth"];
            string[] jobs = [.. LegacySnapshotReviewContract.TerminalJobs];
            string[] services = [.. LegacySnapshotReviewContract.Services];
            string[] repositories = [.. LegacySnapshotReviewContract.Repositories];

            if (mutation == "hangfire-database") databases[0] = "Hangfire";
            if (mutation == "missing-job") jobs = jobs[1..];

            object[] repositoryEvidence = repositories.Select((name, index) => (object)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["commitSha"] = index == 0 ? fixture.AppHostCommit : new string((char)('c' + index % 4), 40),
                ["branch"] = "main",
                ["clean"] = mutation != "dirty-repository" || index != 1,
                ["headMatchesOriginMain"] = mutation != "ahead-repository" || index != 1,
            }).ToArray();

            object[] serviceEvidence = services.Select((name, index) => (object)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["healthy"] = mutation != "unhealthy-service" || index != 0,
                ["readProbe"] = "passed",
            }).ToArray();

            var root = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["status"] = mutation == "failed-status" ? "failed" : "passed",
                ["startedAtUtc"] = completed.AddMinutes(-5),
                ["completedAtUtc"] = completed,
                ["appHostCommit"] = fixture.AppHostCommit,
                ["authoritativeSourceCommitSha"] = mutation == "wrong-source" ? new string('f', 40) : fixture.SourceCommit,
                ["migrationEvidencePayloadSha256"] = mutation == "wrong-migration-evidence" ? new string('a', 64) : fixture.MigrationEvidenceDigest,
                ["approvedBaselineSha256"] = fixture.ApprovedBaselineDigest,
                ["repositoryBaselineSha256"] = mutation == "wrong-repository-baseline" ? new string('a', 64) : fixture.RepositoryBaselineDigest,
                ["snapshot"] = new Dictionary<string, object?>
                {
                    ["id"] = fixture.SnapshotId,
                    ["format"] = mutation == "wrong-snapshot-format" ? "legacy-v1" : "MLVSNP02",
                    ["manifestDigestSha256"] = fixture.ManifestDigest,
                },
                ["repositories"] = repositoryEvidence,
                ["jobs"] = jobs.Select(name => (object)new Dictionary<string, object?> { ["name"] = name, ["state"] = "finished", ["exitCode"] = 0 }).ToArray(),
                ["databases"] = databases,
                ["services"] = serviceEvidence,
                ["constraints"] = new Dictionary<string, object?>
                {
                    ["fixturesEnabled"] = mutation == "fixture-enabled",
                    ["mutatingProbes"] = mutation == "mutating-probe",
                    ["gkeWrites"] = false,
                    ["productionEndpointAccess"] = false,
                },
                ["cleanup"] = "completed",
            };
            if (mutation == "pii-field") root["employeeEmail"] = "person@example.com";
            if (mutation == "count-field") root["rowCount"] = 42;
            File.WriteAllText(path, JsonSerializer.Serialize(root), new UTF8Encoding(false));
            return fixture;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
