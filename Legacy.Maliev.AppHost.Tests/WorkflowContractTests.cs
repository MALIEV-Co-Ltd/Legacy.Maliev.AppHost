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
        Assert.Contains("ref: c0b999745ef45b708e61786df14eae8b95ee0031", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AuthService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 057586a03174930e8831d98dd8538b40690092d0", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CustomerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 6868ec0ed6b87bca399cf1c7883c0de2836e22f9", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.NotificationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 8dc20b7f72cbd96d83736543c7fb69c0aec2a125", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 08eaef2e7c8de366f3e5a57282e1ebea446b43be", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: f37e32592b4ce43ec4c327481b6062c2b6f1a28a", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Web", source, StringComparison.Ordinal);
        Assert.Contains("ref: 838b6e337261753847ae9b263b3c740a3e5453a5", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.EmployeeService", source, StringComparison.Ordinal);
        Assert.Contains("ref: a66005e189eb50ee71f3b961a66380339b9eae3e", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CatalogService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 7d7f7609bfd644259649fb05b4ba5c882769b7e9", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ProcurementService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 9780a28f1172b418df067be090c2b1cfb48e0977", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.FileService", source, StringComparison.Ordinal);
        Assert.Contains("ref: b7f041b4c70546e2f4600feb3a7d202a5c8739b3", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Intranet", source, StringComparison.Ordinal);
        Assert.Contains("ref: 92c9ce747b415853685f3482e4b23c9918b153e1", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CareerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 1e026db743d2105e226ae5e97ce5a50ac425da89", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ContactService", source, StringComparison.Ordinal);
        Assert.Contains("ref: ac8e641a6ee2136574fedc797748ffbcc70440eb", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AccountingService", source, StringComparison.Ordinal);
        Assert.Contains("ref: c0f38c0a5a0505abd13b9250b25c326a833105fe", source, StringComparison.Ordinal);
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

    [Fact]
    public void ReusableWorkflow_GeneratesAndVerifiesTheIntranetBffManifestContract()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "_build-and-test.yml"));
        var verifierPath = Path.Combine(root, "scripts", "verify-intranet-bff-manifest.ps1");

        Assert.Contains("--publisher manifest", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-intranet-bff-manifest.ps1", workflow, StringComparison.Ordinal);
        Assert.True(File.Exists(verifierPath), $"Expected manifest verifier at {verifierPath}.");
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
