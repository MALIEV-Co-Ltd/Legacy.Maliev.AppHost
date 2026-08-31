using System.Diagnostics;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyWebOrchestrationSourceTests
{
    [Fact]
    public async Task ReviewStartScript_RejectsStartupWithoutAuthenticatedShadowSnapshot()
    {
        var root = FindRepositoryRoot();
        var emptyWorkspace = Path.Combine(Path.GetTempPath(), $"maliev-review-no-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyWorkspace);
        try
        {
            var startInfo = new ProcessStartInfo("pwsh")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
                     {
                         "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                         Path.Combine(root, "scripts", "start-local-review-aspire.ps1"),
                         "-WorkspaceRoot", emptyWorkspace,
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell could not be started.");
            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("authenticated v2 shadow snapshot", standardOutput + standardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ProcessId", standardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(emptyWorkspace, recursive: true);
        }
    }

    [Fact]
    public void AppHost_UsesVerifiedWebIdentityAndSourceProjectOverride()
    {
        var root = FindRepositoryRoot();
        var appHost = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));

        Assert.Contains("LegacyWebLaunchIdentity.Capture()", appHost, StringComparison.Ordinal);
        Assert.True(
            appHost.IndexOf("LegacyWebLaunchIdentity.Capture()", StringComparison.Ordinal)
                < appHost.IndexOf("LocalEnvironmentPolicy.SanitizeCurrentProcess()", StringComparison.Ordinal),
            "The verified launch identity must be captured before the ambient environment is sanitized.");
        Assert.Contains("BuildIdentity__Repository", appHost, StringComparison.Ordinal);
        Assert.Contains("BuildIdentity__Branch", appHost, StringComparison.Ordinal);
        Assert.Contains("BuildIdentity__Commit", appHost, StringComparison.Ordinal);
        Assert.Contains("port: legacyWebIdentity.Port", appHost, StringComparison.Ordinal);
        Assert.Contains("$(LegacyMalievWebProject)", project, StringComparison.Ordinal);
        Assert.DoesNotContain(".worktrees", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\bin\\", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\obj\\", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartScript_BuildsExactCleanSourceBeforeNonDestructivePortFailure()
    {
        var root = FindRepositoryRoot();
        var scriptPath = Path.Combine(root, "scripts", "start-current-web.ps1");

        Assert.True(File.Exists(scriptPath), $"Expected deterministic Aspire start script '{scriptPath}'.");
        var script = File.ReadAllText(scriptPath);
        Assert.Contains("'status', '--porcelain'", script, StringComparison.Ordinal);
        Assert.Contains("'rev-parse', 'HEAD'", script, StringComparison.Ordinal);
        Assert.Contains("dotnet build", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection", script, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance Win32_Process", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_WEB_COMMIT", script, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-postgres-username", script, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-postgres-password", script, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-redis-password", script, StringComparison.Ordinal);
        Assert.Contains("-no-build", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Process", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartScript_RequiresExactSnapshotSoExistingEmployeeCredentialsCanAuthenticate()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "start-current-web.ps1"));

        Assert.Contains("[string] $SnapshotDirectory", script, StringComparison.Ordinal);
        Assert.Contains("MALIEV\\legacy-postgres-snapshots", script, StringComparison.Ordinal);
        Assert.Contains("manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT', 'true'", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT_DIR', $SnapshotDirectory", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_FIXTURES', 'false'", script, StringComparison.Ordinal);
        Assert.Contains("Existing-credential local testing requires", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewStartScript_LaunchesPersistentAuthenticatedSnapshotAspireWithDurableLogs()
    {
        var scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "start-local-review-aspire.ps1");

        Assert.True(File.Exists(scriptPath), $"Expected persistent review start script '{scriptPath}'.");
        var script = File.ReadAllText(scriptPath);
        Assert.Contains("Start-Process", script, StringComparison.Ordinal);
        Assert.Contains("-WindowStyle Hidden", script, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardOutput", script, StringComparison.Ordinal);
        Assert.Contains("-RedirectStandardError", script, StringComparison.Ordinal);
        Assert.Contains("MALIEV\\legacy-postgres-snapshots", script, StringComparison.Ordinal);
        Assert.Contains("AES-256-GCM-chunked-v2", script, StringComparison.Ordinal);
        Assert.Contains("snapshot-preflight", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT = 'true'", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT_DIR = $SnapshotDirectory", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE = $SnapshotEncryptionKeyFile", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_SNAPSHOT_ID = $SnapshotId", script, StringComparison.Ordinal);
        Assert.Contains("LEGACY_LOCAL_FIXTURES = 'false'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LEGACY_LOCAL_SNAPSHOT = 'false'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LEGACY_LOCAL_FIXTURES = 'true'", script, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS", script, StringComparison.Ordinal);
        Assert.Contains("http://localhost:15888", script, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-web-google-maps-embed-api-key", script, StringComparison.Ordinal);
        Assert.Contains("Parameters__legacy-intranet-google-maps-browser-api-key", script, StringComparison.Ordinal);
        Assert.Contains("local-review-unconfigured", script, StringComparison.Ordinal);
        Assert.Contains("--no-build", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_DocumentsExactStartAndCoordinatedPortHandoff()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "README.md"));

        Assert.Contains("start-current-web.ps1", readme, StringComparison.Ordinal);
        Assert.Contains("start-local-review-aspire.ps1", readme, StringComparison.Ordinal);
        Assert.Contains("no empty-database or local-fixture fallback", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-WebPort 5188", readme, StringComparison.Ordinal);
        Assert.Contains("-WebPort 5088", readme, StringComparison.Ordinal);
        Assert.Contains("does not terminate", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/web/build-identity", readme, StringComparison.Ordinal);
        Assert.Contains("/about?culture=en", readme, StringComparison.Ordinal);
        Assert.Contains("/about?culture=th", readme, StringComparison.Ordinal);
        Assert.Contains("/InstantQuotation/3D-Printing", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestWorkflow_UsesTheExactVerifiedWebCheckoutIdentity()
    {
        var workflow = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            ".github",
            "workflows",
            "_build-and-test.yml"));

        Assert.Contains("ref: c029ab07d58aec856cceac3942846835780a110e", workflow, StringComparison.Ordinal);
        Assert.Contains("export LEGACY_WEB_PROJECT=", workflow, StringComparison.Ordinal);
        Assert.Contains("export LEGACY_WEB_REPOSITORY=", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGACY_WEB_BRANCH:", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGACY_WEB_BRANCH: codex/localization-review-20260728", workflow, StringComparison.Ordinal);
        Assert.Contains("export LEGACY_WEB_COMMIT=", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGACY_WEB_PORT:", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Legacy.Maliev.AppHost repository root.");
    }
}
