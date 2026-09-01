using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
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
        Assert.Equal(7, LegacySnapshotReviewContract.AuthenticatedReadQueries.Count);
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
        Assert.Contains("Invoke-WebRequest", source, StringComparison.Ordinal);
        Assert.Contains("-Method Get", source, StringComparison.Ordinal);
        Assert.Contains("auth/v1/service/login", source, StringComparison.Ordinal);
        Assert.Contains("ServiceAuthentication__ClientSecret", source, StringComparison.Ordinal);
        Assert.Contains("local-snapshot-review-routes.json", source, StringComparison.Ordinal);
        Assert.Contains("remote get-url origin", source, StringComparison.Ordinal);
        Assert.Contains("FileShare]::None", source, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("--configuration', 'Release", source, StringComparison.Ordinal);
        Assert.Contains("dcpProcessId", source, StringComparison.Ordinal);
        Assert.Contains("localContainerNames", source, StringComparison.Ordinal);
        Assert.Contains("protectedHandles", source, StringComparison.Ordinal);
        Assert.Contains("$validationSucceeded = $false", source, StringComparison.Ordinal);
        Assert.Contains("-not $cleanupCompleted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Move-Item", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clientId = 'legacy-intranet'", source, StringComparison.Ordinal);
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
    [InlineData("failed-http-probe")]
    [InlineData("missing-authenticated-query")]
    [InlineData("wrong-semantic-manifest")]
    [InlineData("unexpected-origin")]
    [InlineData("permissive-evidence-file")]
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

    [Fact]
    public async Task Verifier_PreflightFailureCannotCreatePassedEvidence()
    {
        string directory = Directory.CreateTempSubdirectory("legacy-terminal-failure-").FullName;
        try
        {
            string root = FindRepositoryRoot();
            string missing = Path.Combine(directory, "missing");
            string evidence = Path.Combine(directory, "terminal.json");
            var startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (string argument in new[]
            {
                "-NoLogo", "-NoProfile", "-File", Path.Combine(root, "scripts", "verify-local-snapshot-stack.ps1"),
                "-SnapshotDirectory", missing, "-SnapshotEncryptionKeyFile", missing,
                "-SnapshotId", "exact24-failure-test", "-MigrationEvidencePath", missing,
                "-TrustedPublicKeyPath", missing, "-ExpectedAttestationKeyId", "test-key",
                "-ApprovedBaselinePath", missing, "-ExpectedApprovedBaselineSha256", new string('a', 64),
                "-ConsumptionLedgerPath", Path.Combine(directory, "ledger"),
                "-ExpectedSourceCommitSha", new string('b', 40), "-ExpectedRunId", Guid.NewGuid().ToString("D"),
                "-ExpectedTargetGeneration", "test-generation", "-ExpectedRestoreId", "test-restore",
                "-RequiredAsOfUtc", DateTimeOffset.UtcNow.ToString("O"), "-RepositoryBaselinePath", missing,
                "-ExpectedRepositoryBaselineSha256", new string('c', 64),
                "-ExpectedSemanticManifestDigestSha256", new string('d', 64),
                "-ExpectedManifestFileSha256", new string('e', 64),
                "-EvidencePath", evidence,
            }) startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not start.");
            await process.WaitForExitAsync();
            Assert.NotEqual(0, process.ExitCode);
            Assert.False(File.Exists(evidence));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RouteContract_FreezesServiceSpecificReadinessAndAuthenticatedGetRoutes()
    {
        string root = FindRepositoryRoot();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "contracts", "local-snapshot-review-routes.json")));
        JsonElement services = document.RootElement.GetProperty("services");
        JsonElement queries = document.RootElement.GetProperty("authenticatedQueries");
        Assert.Equal(16, services.GetArrayLength());
        Assert.Equal(7, queries.GetArrayLength());
        Dictionary<string, string> actualRoutes = services.EnumerateArray().ToDictionary(
            item => item.GetProperty("name").GetString()!,
            item => item.GetProperty("readinessPath").GetString()!,
            StringComparer.Ordinal);
        Dictionary<string, string> expectedRoutes = new(StringComparer.Ordinal)
        {
            ["legacy-maliev-country-service"] = "/countries/readiness",
            ["legacy-maliev-document-service"] = "/documents/readiness",
            ["legacy-maliev-auth-service"] = "/auth/readiness",
            ["legacy-maliev-customer-service"] = "/customer/readiness",
            ["legacy-maliev-employee-service"] = "/employee/readiness",
            ["legacy-maliev-catalog-service"] = "/catalog/readiness",
            ["legacy-maliev-procurement-service"] = "/procurement/readiness",
            ["legacy-maliev-file-service"] = "/file/readiness",
            ["legacy-maliev-order-service"] = "/order/readiness",
            ["legacy-maliev-quotation-service"] = "/quotation/readiness",
            ["legacy-maliev-notification-service"] = "/emails/readiness",
            ["legacy-maliev-web"] = "/web/readiness",
            ["legacy-maliev-intranet-bff"] = "/intranet-bff/readiness",
            ["legacy-maliev-career-service"] = "/Jobs/readiness",
            ["legacy-maliev-contact-service"] = "/messages/readiness",
            ["legacy-maliev-accounting-service"] = "/accounting/readiness",
        };
        Assert.Equal(expectedRoutes.OrderBy(item => item.Key), actualRoutes.OrderBy(item => item.Key));
        Assert.Equal(
            LegacySnapshotReviewContract.AuthenticatedReadQueries.Order(),
            queries.EnumerateArray().Select(item => item.GetProperty("id").GetString()).Order());
        Assert.All(queries.EnumerateArray(), item =>
        {
            Assert.Equal("GET", item.GetProperty("method").GetString());
            Assert.StartsWith("/", item.GetProperty("path").GetString());
        });
    }

    [Fact]
    public void Verifier_CannotConstructOrPublishPassedEvidenceBeforeRuntimeAndCleanupSuccess()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "scripts", "verify-local-snapshot-stack.ps1"));
        int guard = source.IndexOf("if (-not $validationSucceeded -or -not $cleanupCompleted)", StringComparison.Ordinal);
        int evidence = source.IndexOf("$terminalEvidence = [ordered]", StringComparison.Ordinal);
        int publisher = source.IndexOf("publish-local-snapshot-terminal-evidence.ps1", StringComparison.Ordinal);
        Assert.True(guard >= 0 && evidence > guard && publisher > evidence);
    }

    [Fact]
    public async Task Publisher_ValidatorFailureLeavesNoPassedArtifact()
    {
        using var fixture = TerminalEvidenceFixture.Create("failed-status");
        string finalPath = Path.Combine(fixture.Directory, "published.json");
        ProcessResult result = await RunPublisherAsync(fixture, finalPath);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(finalPath));
    }

    [Fact]
    public async Task Publisher_ValidCandidateIsPublishedCreateOnly()
    {
        using var fixture = TerminalEvidenceFixture.Create();
        string finalPath = Path.Combine(fixture.Directory, "published.json");
        ProcessResult result = await RunPublisherAsync(fixture, finalPath);
        Assert.True(result.ExitCode == 0, $"stdout: {result.Output}{Environment.NewLine}stderr: {result.Error}");
        Assert.True(File.Exists(finalPath));
        Assert.False(File.Exists(fixture.Path));
    }

    private static async Task<ProcessResult> RunPublisherAsync(TerminalEvidenceFixture fixture, string finalPath)
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
            "-NoLogo", "-NoProfile", "-File", Path.Combine(root, "scripts", "publish-local-snapshot-terminal-evidence.ps1"),
            "-CandidateEvidencePath", fixture.Path, "-FinalEvidencePath", finalPath,
            "-ExpectedAppHostCommit", fixture.AppHostCommit, "-ExpectedSourceCommitSha", fixture.SourceCommit,
            "-ExpectedSnapshotId", fixture.SnapshotId,
            "-ExpectedSemanticManifestDigestSha256", fixture.SemanticManifestDigest,
            "-ExpectedManifestFileSha256", fixture.ManifestFileDigest,
            "-ExpectedMigrationEvidencePayloadSha256", fixture.MigrationEvidenceDigest,
            "-ExpectedApprovedBaselineSha256", fixture.ApprovedBaselineDigest,
            "-ExpectedRepositoryBaselineSha256", fixture.RepositoryBaselineDigest,
        }) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not start.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, output, error);
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
            "-ExpectedSemanticManifestDigestSha256", fixture.SemanticManifestDigest,
            "-ExpectedManifestFileSha256", fixture.ManifestFileDigest,
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
        public string SemanticManifestDigest { get; } = new('1', 64);
        public string ManifestFileDigest { get; } = new('2', 64);
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
            string[] authenticatedQueries = [.. LegacySnapshotReviewContract.AuthenticatedReadQueries];

            if (mutation == "hangfire-database") databases[0] = "Hangfire";
            if (mutation == "missing-job") jobs = jobs[1..];
            if (mutation == "missing-authenticated-query") authenticatedQueries = authenticatedQueries[1..];

            object[] repositoryEvidence = repositories.Select((name, index) => (object)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["commitSha"] = index == 0 ? fixture.AppHostCommit : new string((char)('c' + index % 4), 40),
                ["branch"] = "main",
                ["clean"] = mutation != "dirty-repository" || index != 1,
                ["headMatchesOriginMain"] = mutation != "ahead-repository" || index != 1,
                ["originUrl"] = mutation == "unexpected-origin" && index == 1
                    ? "https://github.com/example/wrong.git"
                    : $"https://github.com/MALIEV-Co-Ltd/{name}.git",
            }).ToArray();

            object[] serviceEvidence = services.Select((name, index) => (object)new Dictionary<string, object?>
            {
                ["name"] = name,
                ["healthy"] = mutation != "unhealthy-service" || index != 0,
                ["probeId"] = "readiness",
                ["probeStatus"] = mutation == "failed-http-probe" && index == 0 ? "failed" : "passed",
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
                    ["semanticManifestDigestSha256"] = mutation == "wrong-semantic-manifest" ? new string('a', 64) : fixture.SemanticManifestDigest,
                    ["manifestFileSha256"] = fixture.ManifestFileDigest,
                },
                ["repositories"] = repositoryEvidence,
                ["jobs"] = jobs.Select(name => (object)new Dictionary<string, object?> { ["name"] = name, ["state"] = "finished", ["exitCode"] = 0 }).ToArray(),
                ["databases"] = databases,
                ["services"] = serviceEvidence,
                ["authenticatedQueries"] = authenticatedQueries.Select(id => (object)new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["status"] = "passed",
                }).ToArray(),
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
            if (mutation != "permissive-evidence-file") SecureOwnerOnly(path);
            return fixture;
        }

        private static void SecureOwnerOnly(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
                var security = new FileSecurity();
                security.SetOwner(owner);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
