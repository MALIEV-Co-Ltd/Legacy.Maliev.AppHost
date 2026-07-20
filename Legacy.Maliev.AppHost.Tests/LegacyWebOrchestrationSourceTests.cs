namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyWebOrchestrationSourceTests
{
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
    public void Readme_DocumentsExactStartAndCoordinatedPortHandoff()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "README.md"));

        Assert.Contains("start-current-web.ps1", readme, StringComparison.Ordinal);
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

        Assert.Contains("ref: 345176b3e6d977e1428afc227eddad7267b147aa", workflow, StringComparison.Ordinal);
        Assert.Contains("export LEGACY_WEB_PROJECT=", workflow, StringComparison.Ordinal);
        Assert.Contains("export LEGACY_WEB_REPOSITORY=", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGACY_WEB_BRANCH:", workflow, StringComparison.Ordinal);
        Assert.Contains("LEGACY_WEB_BRANCH: main", workflow, StringComparison.Ordinal);
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
