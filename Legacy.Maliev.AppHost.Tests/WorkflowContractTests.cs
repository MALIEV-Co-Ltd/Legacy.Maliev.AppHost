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
        Assert.Contains("ref: b643ce6480409d38935956a9db6f2a5908d326de", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 632c8494439815b91b01e7b69b325790e91a2355", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AuthService", source, StringComparison.Ordinal);
        Assert.Contains("ref: ad49105da8ddf416b1f097f607d475b1c634589c", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CustomerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: de5de156276b7380ae2de1c95fbec19af59a43b1", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.NotificationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 218170e2d398ffe5837173f9e0912618d7532406", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 2766fd61a556277e1911f2696cfb9a66dc53e0d2", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 14fa0cae2779f80a75cc0c3cfb09cbedbeed76fc", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults", source, StringComparison.Ordinal);
        Assert.Contains("ref: 3152a9612d8514597192a98eae31277aef8102ff", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.ServiceDefaults", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts", source, StringComparison.Ordinal);
        Assert.Contains("ref: 13eefdb44cad42b46216bb0378af8c76e3672c2c", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.CompatibilityContracts", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Web", source, StringComparison.Ordinal);
        Assert.Contains("ref: c029ab07d58aec856cceac3942846835780a110e", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.EmployeeService", source, StringComparison.Ordinal);
        Assert.Contains("ref: e5b6f8ad12964d6f57713b375c65de52bb44ca9d", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CatalogService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 66342d5543373b17d309ac125216b959ea9e626f", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ProcurementService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 26d7ab3e3963ea5127998e53a98af82d4a54176f", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.FileService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 20f7c65d6ac5c7456a771287b0a1728a280d0e41", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Intranet", source, StringComparison.Ordinal);
        Assert.Contains("ref: 440af8feaf5fdf09b435e70a1bdf6c8cf706ea44", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CareerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 56b052d577b8ff83d36204df01c5c2241660c57b", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ContactService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 1d145919a9e89e56b36c44b43e43b606f38a3de5", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AccountingService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 332f77e8550f4deae1efab60931b4f6505ea69a9", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MALIEV-Co-Ltd/Maliev.Aspire", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MALIEV-Co-Ltd/Maliev.MessagingContracts", source, StringComparison.Ordinal);
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

    [Fact]
    public void ReusableWorkflow_VerifiesTheAccountingIdentityManifestContract()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "_build-and-test.yml"));
        var verifierPath = Path.Combine(root, "scripts", "verify-accounting-identity-manifest.ps1");

        Assert.Contains("verify-accounting-identity-manifest.ps1", workflow, StringComparison.Ordinal);
        Assert.True(File.Exists(verifierPath), $"Expected manifest verifier at {verifierPath}.");

        var verifier = File.ReadAllText(verifierPath);
        foreach (var permission in new[]
        {
            "legacy.documents.render",
            "legacy-file.uploads.create",
            "legacy-file.uploads.read",
            "legacy-file.uploads.delete",
            "legacy.notifications.send",
            "legacy-customer.customers.read",
            "legacy-employee.signatures.read",
            "legacy.quotations.read",
            "legacy.customer-quotations.read",
            "legacy.quotation-lines.read",
            "legacy.quotations.update",
            "legacy-employee.employees.read",
            "legacy-catalog.currencies.read",
            "legacy-catalog.countries.read",
        })
        {
            Assert.Contains(permission, verifier, StringComparison.Ordinal);
        }

        Assert.Contains("${permissionPrefix}14", verifier, StringComparison.Ordinal);
        Assert.Contains("must not receive a fifteenth permission", verifier, StringComparison.Ordinal);
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
