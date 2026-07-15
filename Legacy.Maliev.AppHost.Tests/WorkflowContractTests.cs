using System.Text.RegularExpressions;

namespace Legacy.Maliev.AppHost.Tests;

public sealed partial class WorkflowContractTests
{
    private static readonly string[] RequiredWorkflows =
    [
        "_build-and-test.yml",
        "ci-develop.yml",
        "ci-main.yml",
        "ci-staging.yml",
        "pr-validation.yml",
    ];

    [Fact]
    public void Workflows_AreValidationOnlyAndLeastPrivilege()
    {
        var workflowDirectory = Path.Combine(FindRepositoryRoot(), ".github", "workflows");

        foreach (var workflowName in RequiredWorkflows)
        {
            var workflowPath = Path.Combine(workflowDirectory, workflowName);
            Assert.True(File.Exists(workflowPath), $"Expected workflow at {workflowPath}.");
            var source = File.ReadAllText(workflowPath);

            Assert.Contains("contents: read", source, StringComparison.Ordinal);
            Assert.Contains("concurrency:", source, StringComparison.Ordinal);
            Assert.DoesNotContain("id-token: write", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gcloud", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kubectl apply", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("argocd", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReusableWorkflow_PinsActionsAndSiblingRepositories()
    {
        var workflowPath = Path.Combine(
            FindRepositoryRoot(),
            ".github",
            "workflows",
            "_build-and-test.yml");
        Assert.True(File.Exists(workflowPath), $"Expected workflow at {workflowPath}.");
        var source = File.ReadAllText(workflowPath);

        Assert.Contains("Legacy.Maliev.CountryService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("ref: b7492444e0a3960778ab19d554a6bc17158cfd80", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AuthService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 6a868d4d520c55534e350a21f31128c94c4ba6da", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CustomerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 7808667a3b4560e4027f0a7f0905b644afc70b57", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.NotificationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 8dc20b7f72cbd96d83736543c7fb69c0aec2a125", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 3088a4660ed9c918daee4ec36a302fd8536bf10d", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: f37e32592b4ce43ec4c327481b6062c2b6f1a28a", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Web", source, StringComparison.Ordinal);
        Assert.Contains("ref: 838b6e337261753847ae9b263b3c740a3e5453a5", source, StringComparison.Ordinal);
        Assert.Contains("Maliev.Aspire", source, StringComparison.Ordinal);
        Assert.Contains("Maliev.MessagingContracts", source, StringComparison.Ordinal);
        Assert.Contains(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@6017816fa67f369d785ed30794f002cfd6299af7",
            source,
            StringComparison.Ordinal);
        Assert.Contains("          working-directory: Legacy.Maliev.AppHost", source, StringComparison.Ordinal);
        Assert.Contains("          solution: Legacy.Maliev.AppHost.slnx", source, StringComparison.Ordinal);
        Assert.Contains("          use-local-maliev-dependencies: 'true'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/setup-dotnet@", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actions/cache@", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(DuplicatedDotnetValidationRegex(), source);
        Assert.DoesNotContain("GITHUB_ACTIONS=false dotnet", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(UnpinnedActionRegex(), source);
    }

    [Fact]
    public void Dependabot_MonitorsNuGetAndActions()
    {
        var dependabotPath = Path.Combine(FindRepositoryRoot(), ".github", "dependabot.yml");
        Assert.True(File.Exists(dependabotPath), $"Expected Dependabot configuration at {dependabotPath}.");
        var source = File.ReadAllText(dependabotPath);

        Assert.Contains("package-ecosystem: nuget", source, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: github-actions", source, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"uses:\s+[^\s@]+@(?!(?:[0-9a-f]{40})(?:\s|$))[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UnpinnedActionRegex();

    [GeneratedRegex(@"run:\s*(?:GITHUB_ACTIONS=false\s+)?dotnet\s+(?:restore|build|test|format|list)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DuplicatedDotnetValidationRegex();

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
