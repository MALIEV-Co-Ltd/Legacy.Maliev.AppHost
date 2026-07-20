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
        Assert.Contains("ref: a56a2cadb55aba93026cb5b7dbb8bb0e94597df5", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AuthService", source, StringComparison.Ordinal);
        Assert.Contains("ref: abbe40e494ee77ba10c82331847073f97f2ab6e7", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CustomerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: bd7ae25ac47ac107a641f5ff06c0f9041a306c82", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.NotificationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: c573988b97c9099c5148bf038c518ee263ecdef3", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 37749e2e12016864376b3df7a6da2cd09905cf3d", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 3074be18147a120a7ada06a252a11171c2731667", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults", source, StringComparison.Ordinal);
        Assert.Contains("ref: 67cd84ccd47be656383b0025e9f2b8d1d3f0eb36", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.ServiceDefaults", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts", source, StringComparison.Ordinal);
        Assert.Contains("ref: 95c62eb6209411f5aada443b315447a2f76ca0cd", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.CompatibilityContracts", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Web", source, StringComparison.Ordinal);
        Assert.Contains("ref: 345176b3e6d977e1428afc227eddad7267b147aa", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.EmployeeService", source, StringComparison.Ordinal);
        Assert.Contains("ref: f901f601f661d54d394d263191a9180d0c37182a", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CatalogService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 97572d02ba6d1341338e749c215444fdf13868e8", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ProcurementService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 7a3cd802b1b35994c4bb59d29d46ac07450794a3", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.FileService", source, StringComparison.Ordinal);
        Assert.Contains("ref: bd7cd339647f06363ff2e66d87214cf50cae1bd9", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Intranet", source, StringComparison.Ordinal);
        Assert.Contains("ref: 9d23f25cc965d98876e20aabed480560d0b653a4", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CareerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 174fce682c7809706174bcc2fb5b80d8e6b04da1", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ContactService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 20c7705a0fdec20436f27913a8939d6cd8f94b5e", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AccountingService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 12a5d74a13dbe72ff72bb704f3ac09547aca6920", source, StringComparison.Ordinal);
        Assert.Contains("Maliev.Aspire", source, StringComparison.Ordinal);
        Assert.Contains("Maliev.MessagingContracts", source, StringComparison.Ordinal);
        Assert.Contains(
            "ref: 9c41d6524a485bf03ba022b8170f47366ab1a77a",
            source,
            StringComparison.Ordinal);
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
