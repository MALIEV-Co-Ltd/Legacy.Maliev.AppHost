using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LocalVerificationEvidenceContractTests
{

    [Theory]
    [InlineData("passed", "", true)]
    [InlineData("failed", "verification", false)]
    [InlineData("failed", "cleanup", false)]
    public async Task EvidenceWriter_TerminalResult_WritesSafeFailClosedArtifact(
        string result,
        string failureCategory,
        bool expectedPassed)
    {
        var root = FindRepositoryRoot();
        var commit = await GetRepositoryCommitAsync(root);
        var script = Path.Combine(root, "scripts", "write-local-verification-evidence.ps1");
        var output = Path.Combine(Path.GetTempPath(), $"legacy-evidence-{Guid.NewGuid():N}.json");

        try
        {
            var arguments = new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-File", script,
                "-OutputPath", output,
                "-Result", result,
                "-CurrentStage", expectedPassed ? "complete" : "verification",
                "-CompletedStages", "preflight,build,orchestration,verification",
                "-FailureCategory", failureCategory,
                "-StartedAtUtc", "2026-07-19T00:00:00Z",
                "-FinishedAtUtc", "2026-07-19T00:01:00Z",
                "-AppHostCommit", commit,
                "-CleanupCompleted",
            };

            var execution = await RunPowerShellAsync(arguments);
            Assert.Equal(0, execution.ExitCode);
            Assert.True(File.Exists(output));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var rootElement = document.RootElement;
            Assert.Equal(1, rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(result, rootElement.GetProperty("result").GetString());
            Assert.Equal(commit, rootElement.GetProperty("source").GetProperty("commit").GetString());
            Assert.Equal(
                expectedPassed ? JsonValueKind.Null : JsonValueKind.String,
                rootElement.GetProperty("verification").GetProperty("failureCategory").ValueKind);
            Assert.True(rootElement.GetProperty("verification").GetProperty("cleanupCompleted").GetBoolean());

            var constraints = rootElement.GetProperty("constraints");
            Assert.Equal("local-disposable", constraints.GetProperty("environment").GetString());
            Assert.Equal("maliev-legacy", constraints.GetProperty("kubernetesNamespace").GetString());
            Assert.Equal(0, constraints.GetProperty("cutoverPercent").GetInt32());
            Assert.False(constraints.GetProperty("productionDeploymentAllowed").GetBoolean());
            Assert.False(constraints.GetProperty("productionDataWritesAllowed").GetBoolean());
            Assert.False(constraints.GetProperty("newNodePoolAllowed").GetBoolean());
            Assert.False(constraints.GetProperty("cloudSqlAllowed").GetBoolean());
            Assert.False(constraints.GetProperty("additionalInfrastructureCostAllowed").GetBoolean());

            var serialized = rootElement.GetRawText();
            Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connectionstring", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", serialized, StringComparison.OrdinalIgnoreCase);

            var validation = await RunPowerShellAsync([
                "-NoLogo", "-NoProfile", "-File",
                Path.Combine(root, "scripts", "test-local-verification-evidence.ps1"),
                "-EvidencePath", output,
                "-ExpectedCommit", commit,
            ]);
            Assert.Equal(expectedPassed ? 0 : 1, validation.ExitCode);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task EvidenceWriter_FailedWithoutControlledCategory_RejectsArtifact()
    {
        var root = FindRepositoryRoot();
        var commit = await GetRepositoryCommitAsync(root);
        var script = Path.Combine(root, "scripts", "write-local-verification-evidence.ps1");
        var output = Path.Combine(Path.GetTempPath(), $"legacy-evidence-{Guid.NewGuid():N}.json");

        try
        {
            var execution = await RunPowerShellAsync([
                "-NoLogo", "-NoProfile", "-File", script,
                "-OutputPath", output,
                "-Result", "failed",
                "-CurrentStage", "verification",
                "-StartedAtUtc", "2026-07-19T00:00:00Z",
                "-FinishedAtUtc", "2026-07-19T00:01:00Z",
                "-AppHostCommit", commit,
            ]);

            Assert.NotEqual(0, execution.ExitCode);
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task EvidenceWriter_RunningWithoutFinish_WritesIncompleteArtifact()
    {
        var root = FindRepositoryRoot();
        var commit = await GetRepositoryCommitAsync(root);
        var script = Path.Combine(root, "scripts", "write-local-verification-evidence.ps1");
        var output = Path.Combine(Path.GetTempPath(), $"legacy-evidence-{Guid.NewGuid():N}.json");

        try
        {
            var execution = await RunPowerShellAsync([
                "-NoLogo", "-NoProfile", "-File", script,
                "-OutputPath", output,
                "-Result", "running",
                "-CurrentStage", "preflight",
                "-StartedAtUtc", "2026-07-19T00:00:00Z",
                "-AppHostCommit", commit,
            ]);
            Assert.Equal(0, execution.ExitCode);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.Equal("running", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("timing").GetProperty("finishedAtUtc").ValueKind);

            var validation = await RunPowerShellAsync([
                "-NoLogo", "-NoProfile", "-File",
                Path.Combine(root, "scripts", "test-local-verification-evidence.ps1"),
                "-EvidencePath", output,
                "-ExpectedCommit", commit,
            ]);
            Assert.NotEqual(0, validation.ExitCode);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("commit-mismatch")]
    [InlineData("malformed-time")]
    [InlineData("reversed-time")]
    [InlineData("missing-stage")]
    public async Task EvidenceValidator_TamperedArtifact_Rejects(string mutation)
    {
        var root = FindRepositoryRoot();
        var commit = await GetRepositoryCommitAsync(root);
        var validator = Path.Combine(root, "scripts", "test-local-verification-evidence.ps1");
        var output = Path.Combine(Path.GetTempPath(), $"legacy-evidence-{Guid.NewGuid():N}.json");

        try
        {
            var evidence = CreateValidEvidence(commit);
            switch (mutation)
            {
                case "unknown-field":
                    evidence["unexpected"] = true;
                    break;
                case "commit-mismatch":
                    evidence["source"]!["commit"] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
                    break;
                case "malformed-time":
                    evidence["timing"]!["startedAtUtc"] = "not-a-date";
                    break;
                case "reversed-time":
                    evidence["timing"]!["startedAtUtc"] = "2026-07-19T00:02:00.0000000+00:00";
                    break;
                case "missing-stage":
                    evidence["verification"]!["completedStages"] = new JsonArray("verification");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            await File.WriteAllTextAsync(output, evidence.ToJsonString());
            var execution = await RunPowerShellAsync([
                "-NoLogo", "-NoProfile", "-File", validator,
                "-EvidencePath", output,
                "-ExpectedCommit", commit,
            ]);

            Assert.NotEqual(0, execution.ExitCode);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static JsonObject CreateValidEvidence(string commit) => new()
    {
        ["schemaVersion"] = 1,
        ["result"] = "passed",
        ["source"] = new JsonObject
        {
            ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.AppHost",
            ["commit"] = commit,
        },
        ["timing"] = new JsonObject
        {
            ["startedAtUtc"] = "2026-07-19T00:00:00.0000000+00:00",
            ["finishedAtUtc"] = "2026-07-19T00:01:00.0000000+00:00",
        },
        ["verification"] = new JsonObject
        {
            ["currentStage"] = "complete",
            ["completedStages"] = new JsonArray("preflight", "build", "orchestration", "verification"),
            ["failureCategory"] = null,
            ["cleanupCompleted"] = true,
        },
        ["constraints"] = new JsonObject
        {
            ["environment"] = "local-disposable",
            ["kubernetesNamespace"] = "maliev-legacy",
            ["cutoverPercent"] = 0,
            ["productionDeploymentAllowed"] = false,
            ["productionDataWritesAllowed"] = false,
            ["newNodePoolAllowed"] = false,
            ["cloudSqlAllowed"] = false,
            ["additionalInfrastructureCostAllowed"] = false,
        },
    };

    [Fact]
    public void LocalVerifier_RecordsRunningAndTerminalEvidenceWithoutRawExceptionData()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "scripts", "verify-local-stack.ps1"));

        Assert.Contains("Write-VerificationEvidence -Result running", source, StringComparison.Ordinal);
        Assert.Contains("Write-VerificationEvidence -Result passed", source, StringComparison.Ordinal);
        Assert.Contains("Write-VerificationEvidence -Result failed", source, StringComparison.Ordinal);
        Assert.Contains("$verificationFailureCategory = switch ($verificationCurrentStage)", source, StringComparison.Ordinal);
        Assert.Contains("git -C $repositoryRoot status --porcelain=v1 --untracked-files=all", source, StringComparison.Ordinal);
        Assert.Contains("source tree must be clean before local verification evidence is created", source, StringComparison.Ordinal);
        Assert.Contains("$cleanupCompleted = $cleanupFailures.Count -eq 0", source, StringComparison.Ordinal);
        Assert.Contains("$verificationFailureCategory = 'cleanup'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-FailureDetail", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-ErrorMessage", source, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string StandardError)> RunPowerShellAsync(
        IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardError);
    }

    private static async Task<string> GetRepositoryCommitAsync(string root)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(root);
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git could not be started.");
        var commit = (await process.StandardOutput.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Matches("^[0-9a-f]{40}$", commit);
        return commit;
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
}
