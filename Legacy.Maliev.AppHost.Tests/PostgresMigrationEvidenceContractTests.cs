using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class PostgresMigrationEvidenceContractTests
{
    [Fact]
    public async Task Validator_AcceptsExactSourceBackedParity()
    {
        using var evidence = TemporaryEvidence.Create();
        var result = await RunValidatorAsync(evidence.Path, "2026-08-07T00:00:00Z");

        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Theory]
    [InlineData("source-stale")]
    [InlineData("target-before-source")]
    [InlineData("row-count-drift")]
    [InlineData("schema-drift")]
    [InlineData("data-drift")]
    [InlineData("duplicate-database")]
    [InlineData("unknown-field")]
    [InlineData("sensitive-field")]
    [InlineData("future-source")]
    public async Task Validator_RejectsTamperedOrUnsafeEvidence(string mutation)
    {
        using var evidence = TemporaryEvidence.Create(mutation);
        var result = await RunValidatorAsync(evidence.Path, "2026-08-07T00:00:00Z");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Validator_RejectsNonUtcRequiredCutoff()
    {
        using var evidence = TemporaryEvidence.Create();
        var result = await RunValidatorAsync(evidence.Path, "2026-08-07T07:00:00+07:00");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void WorkflowAndDocs_ExposeTheReadOnlyParityValidator()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify-postgres-migration-evidence.ps1"));
        var docs = File.ReadAllText(Path.Combine(root, "docs", "postgres-migration-evidence.md"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("-RequiredAsOfUtc", docs, StringComparison.Ordinal);
        Assert.Contains("docs/postgres-migration-evidence.md", readme, StringComparison.Ordinal);
        Assert.Contains("ExpectedDatabase", script, StringComparison.Ordinal);
        Assert.Contains("productionDataWritesAllowed", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoSensitiveKeys", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kubectl apply", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gcloud", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlcmd", script, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string StandardError)> RunValidatorAsync(
        string evidencePath,
        string requiredAsOfUtc)
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "scripts", "verify-postgres-migration-evidence.ps1");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-File", script,
            "-EvidencePath", evidencePath,
            "-ExpectedDatabase", "Country,Customer",
            "-RequiredAsOfUtc", requiredAsOfUtc,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardError);
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

    private sealed class TemporaryEvidence : IDisposable
    {
        private readonly string _directory;

        private TemporaryEvidence(string path, string directory)
        {
            Path = path;
            _directory = directory;
        }

        public string Path { get; }

        public static TemporaryEvidence Create(string? mutation = null)
        {
            var directory = Directory.CreateTempSubdirectory("legacy-postgres-parity-");
            var root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["source"] = new JsonObject
                {
                    ["system"] = "sqlserver",
                    ["snapshotId"] = "source-2026-08-07",
                    ["capturedAtUtc"] = "2026-08-07T00:05:00.0000000+00:00",
                    ["backupUri"] = "gs://maliev.com/database/full/2026-08-07/source.bak",
                },
                ["target"] = new JsonObject
                {
                    ["system"] = "postgresql",
                    ["cluster"] = "legacy-postgres-main",
                    ["namespace"] = "maliev-legacy",
                    ["capturedAtUtc"] = "2026-08-07T00:30:00.0000000+00:00",
                    ["restoreId"] = "restore-2026-08-07",
                },
                ["databases"] = new JsonArray(
                    Database("Country", "a"),
                    Database("Customer", "b")),
                ["parity"] = "exact",
                ["constraints"] = new JsonObject
                {
                    ["productionDataWritesAllowed"] = false,
                    ["cutoverPercent"] = 0,
                    ["newNodePoolAllowed"] = false,
                    ["cloudSqlAllowed"] = false,
                    ["additionalInfrastructureCostAllowed"] = false,
                },
            };

            switch (mutation)
            {
                case "source-stale":
                    ((JsonObject)root["source"]!)["capturedAtUtc"] = "2026-08-06T23:59:59.0000000+00:00";
                    break;
                case "target-before-source":
                    ((JsonObject)root["target"]!)["capturedAtUtc"] = "2026-08-07T00:04:59.0000000+00:00";
                    break;
                case "row-count-drift":
                    ((JsonObject)((JsonArray)root["databases"]!)[1]!)["targetRowCount"] = 99;
                    break;
                case "schema-drift":
                    ((JsonObject)((JsonArray)root["databases"]!)[0]!)["targetSchemaSha256"] = new string('c', 64);
                    break;
                case "data-drift":
                    ((JsonObject)((JsonArray)root["databases"]!)[1]!)["targetDataSha256"] = new string('c', 64);
                    break;
                case "duplicate-database":
                    ((JsonArray)root["databases"]!).Add(Database("Country", "c"));
                    break;
                case "unknown-field":
                    root["unexpected"] = true;
                    break;
                case "sensitive-field":
                    ((JsonObject)root["source"]!)["apiToken"] = "must-not-be-recorded";
                    break;
                case "future-source":
                    ((JsonObject)root["source"]!)["capturedAtUtc"] = "2099-08-07T00:05:00.0000000+00:00";
                    break;
            }

            var path = System.IO.Path.Combine(directory.FullName, "evidence.json");
            File.WriteAllText(path, root.ToJsonString());
            return new TemporaryEvidence(path, directory.FullName);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static JsonObject Database(string name, string seed)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["sourceRowCount"] = 10,
                ["targetRowCount"] = 10,
                ["sourceSchemaSha256"] = new string(seed[0], 64),
                ["targetSchemaSha256"] = new string(seed[0], 64),
                ["sourceDataSha256"] = new string(seed[0], 64),
                ["targetDataSha256"] = new string(seed[0], 64),
                ["parity"] = "exact",
            };
        }
    }
}
