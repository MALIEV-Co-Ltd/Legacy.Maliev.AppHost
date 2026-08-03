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
        Assert.Contains("ref: 755e9236114fb5cab73cc1667f3773eea32ba167", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.DocumentService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AuthService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 9817cebd37354505073e75bf1954886a44bf74a8", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CustomerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 1c488b344b3c97f8c0b3f356db8291ac3676f138", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.NotificationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 313c3fdc5568422218873e73b6f42c73763649e9", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("ref: bc880a1da771051b8caf8c135c0633ea43d1257e", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.OrderService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 5f02d81ed7369bd25bd85554d4d28f63aeb7c855", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.QuotationService", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults", source, StringComparison.Ordinal);
        Assert.Contains("ref: 25e2502a3146b0bbb5fec12731db89d20702f2cc", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.ServiceDefaults", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts", source, StringComparison.Ordinal);
        Assert.Contains("ref: 78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7", source, StringComparison.Ordinal);
        Assert.Contains("path: Legacy.Maliev.CompatibilityContracts", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Web", source, StringComparison.Ordinal);
        Assert.Contains("ref: f1dbbc5d4fe50fd1256a9371a1e9f14a8f012432", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.EmployeeService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 0e2e8d6c35cee95857a8b6f324623c4bf4530fdc", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CatalogService", source, StringComparison.Ordinal);
        Assert.Contains("ref: d0e9cd8abd54c4a6d86ff20a64cff0520ffd3687", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ProcurementService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 615208f72e91e44fa7703a67d07e8152977f23b4", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.FileService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 1fd9a8ef8713945984c3d17d1a327f086246976f", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.Intranet", source, StringComparison.Ordinal);
        Assert.Contains("ref: 7e632a47f02a9851c2ac618c6768b6d0174e7b78", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.CareerService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 22f3697a42b89cbc2aabff85e80b276f281c1e92", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.ContactService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 21c34697c08af93455b8753c9c360f9d9724024d", source, StringComparison.Ordinal);
        Assert.Contains("repository: MALIEV-Co-Ltd/Legacy.Maliev.AccountingService", source, StringComparison.Ordinal);
        Assert.Contains("ref: 01573c0d027e347b5fbdc8a2468c7100757ba502", source, StringComparison.Ordinal);
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
