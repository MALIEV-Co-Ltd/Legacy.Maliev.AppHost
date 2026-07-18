using System.Text.Json;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class OwnerReviewPackageContractTests
{
    private const string AppHostBaseline = "7bf160457cc984b753609f9f7a43e45a23d7168f";
    private const string IntranetBaseline = "dfb99b3b3af5b7fd6ed2d6d8bf93379495df68e1";
    private const string DocumentBaseline = "a56a2cadb55aba93026cb5b7dbb8bb0e94597df5";
    private const string GitOpsBaseline = "a7eb1c48a320669eceaf92011d3a08b06a17be23";

    [Fact]
    public void OwnerReviewPackage_RecordsExactCompletedEvidence()
    {
        using var package = LoadPackage();
        var root = package.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AppHostBaseline, ReadString(root, "baseline", "appHostCommit"));
        Assert.Equal(
            "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.AppHost/actions/runs/29647507804",
            ReadString(root, "baseline", "ciUrl"));
        Assert.Equal(56, ReadInt(root, "baseline", "servicePinsIssue"));
        Assert.False(ReadBoolean(root, "baseline", "servicePinsChanged"));

        var completed = root.GetProperty("completedEvidence");
        AssertEvidence(completed.GetProperty("intranet"), IntranetBaseline, 486, 0);
        Assert.Equal(
            "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Intranet/actions/runs/29643178549",
            completed.GetProperty("intranet").GetProperty("ciUrl").GetString());

        AssertEvidence(completed.GetProperty("documentService"), DocumentBaseline, 83, 22);
        Assert.Equal(
            "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.DocumentService/actions/runs/29651958706",
            completed.GetProperty("documentService").GetProperty("ciUrl").GetString());

        var gitOps = completed.GetProperty("gitOpsReadiness");
        Assert.Equal(GitOpsBaseline, gitOps.GetProperty("commit").GetString());
        Assert.Equal(26, gitOps.GetProperty("contractTestsPassed").GetInt32());
        Assert.Equal(
            "https://github.com/MALIEV-Co-Ltd/maliev-gitops/actions/runs/29650766400",
            gitOps.GetProperty("ciUrl").GetString());
        Assert.False(gitOps.GetProperty("syncedToCluster").GetBoolean());

        var repositories = completed.GetProperty("repositories");
        Assert.Equal(20, repositories.GetProperty("legacyRepositoryCount").GetInt32());
        Assert.True(repositories.GetProperty("allPublic").GetBoolean());
        Assert.True(repositories.GetProperty("allMainBranchesProtected").GetBoolean());
        Assert.Equal("validate / validate", repositories.GetProperty("requiredCheck").GetString());
        Assert.Equal(0, repositories.GetProperty("requiredApprovals").GetInt32());
    }

    [Fact]
    public void OwnerReviewPackage_FailsClosedWhileRequiredReleaseGatesArePending()
    {
        using var package = LoadPackage();
        var root = package.RootElement;
        var pending = root.GetProperty("pendingGates");

        AssertIssueIsOpen(pending, "instantQuotation", "Legacy.Maliev.Web", 148);
        AssertIssueIsOpen(pending, "instantQuotation", "Legacy.Maliev.Web", 149);
        AssertIssueIsOpen(pending, "instantQuotation", "Legacy.Maliev.Web", 150);
        AssertIssueIsOpen(pending, "instantQuotation", "Legacy.Maliev.Web", 151);
        AssertIssueIsOpen(pending, "instantQuotation", "Legacy.Maliev.Web", 152);
        AssertIssueIsOpen(pending, "instantQuotation", "Legacy.Maliev.Web", 153);
        AssertIssueIsOpen(pending, "fileService", "Legacy.Maliev.FileService", 7);

        foreach (var gate in new[]
        {
            "containerAndDestinationMalwareScanClean",
            "safeBrowsingClean",
            "searchConsoleSecurityIssuesClean",
            "gtmPreviewValidated",
            "ga4DebugViewValidated",
            "googleAdsConversionValidated",
        })
        {
            Assert.False(pending.GetProperty("googleReleaseChecks").GetProperty(gate).GetBoolean(), gate);
        }

        foreach (var gate in new[]
        {
            "existingClusterCapacityRecorded",
            "backupAndWalRecoveryDrilled",
            "shadowAndFinalParityRecorded",
            "rollbackRehearsed",
            "aspireOwnerReviewCompleted",
        })
        {
            Assert.False(pending.GetProperty("cloudNativePostgres").GetProperty(gate).GetBoolean(), gate);
        }

        var constraints = root.GetProperty("constraints");
        Assert.Equal("maliev-legacy", constraints.GetProperty("namespace").GetString());
        Assert.False(constraints.GetProperty("newNodePoolAllowed").GetBoolean());
        Assert.False(constraints.GetProperty("cloudSqlAllowed").GetBoolean());
        Assert.False(constraints.GetProperty("additionalInfrastructureCostAllowed").GetBoolean());

        var release = root.GetProperty("releaseDecision");
        Assert.Equal(0, release.GetProperty("cutoverPercent").GetInt32());
        Assert.False(release.GetProperty("ownerApproved").GetBoolean());
        Assert.False(release.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.False(release.GetProperty("ingressOrDnsChangeAllowed").GetBoolean());
        Assert.False(release.GetProperty("productionSiteReplacementAllowed").GetBoolean());

        foreach (var gate in new[]
        {
            "serviceRollbackRehearsed",
            "databaseRollbackRehearsed",
            "webIngressRollbackRehearsed",
            "analyticsRollbackRehearsed",
        })
        {
            Assert.False(release.GetProperty("rollbackPlan").GetProperty(gate).GetBoolean(), gate);
        }
    }

    [Fact]
    public void OwnerReviewChecklist_IsLinkedFromTheReadmeAndNamesTheMachineReadablePackage()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var checklistPath = Path.Combine(root, "docs", "owner-review-checklist.md");

        Assert.True(File.Exists(checklistPath), $"Expected owner checklist at '{checklistPath}'.");
        Assert.Contains("docs/owner-review-checklist.md", readme, StringComparison.Ordinal);
        Assert.Contains("owner-review-evidence.json", File.ReadAllText(checklistPath), StringComparison.Ordinal);
    }

    private static JsonDocument LoadPackage()
    {
        var path = Path.Combine(FindRepositoryRoot(), "docs", "owner-review-evidence.json");
        Assert.True(File.Exists(path), $"Expected owner-review evidence package at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static void AssertEvidence(JsonElement evidence, string commit, int testsPassed, int rasterFixtures)
    {
        Assert.Equal(commit, evidence.GetProperty("commit").GetString());
        Assert.Equal(testsPassed, evidence.GetProperty("testsPassed").GetInt32());
        if (rasterFixtures > 0)
        {
            Assert.Equal(rasterFixtures, evidence.GetProperty("rasterFixtures").GetInt32());
            Assert.Equal(150, evidence.GetProperty("rasterDpi").GetInt32());
            Assert.True(evidence.GetProperty("thaiToneMarkCropsPassed").GetBoolean());
        }
    }

    private static void AssertIssueIsOpen(JsonElement pending, string group, string repository, int number)
    {
        var issue = pending
            .GetProperty(group)
            .EnumerateArray()
            .Single(candidate => candidate.GetProperty("repository").GetString() == repository
                && candidate.GetProperty("number").GetInt32() == number);

        Assert.Equal("open", issue.GetProperty("state").GetString());
        Assert.False(issue.GetProperty("complete").GetBoolean());
    }

    private static string ReadString(JsonElement root, string group, string property) =>
        root.GetProperty(group).GetProperty(property).GetString()
        ?? throw new InvalidDataException($"{group}.{property} must be a string.");

    private static int ReadInt(JsonElement root, string group, string property) =>
        root.GetProperty(group).GetProperty(property).GetInt32();

    private static bool ReadBoolean(JsonElement root, string group, string property) =>
        root.GetProperty(group).GetProperty(property).GetBoolean();

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
