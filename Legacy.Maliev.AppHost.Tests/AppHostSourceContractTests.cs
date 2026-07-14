namespace Legacy.Maliev.AppHost.Tests;

public sealed class AppHostSourceContractTests
{
    [Fact]
    public void AppHost_ModelsTheDormantLegacyRuntimeWithoutCloudDeployment()
    {
        var sourcePath = Path.Combine(FindRepositoryRoot(), "Legacy.Maliev.AppHost", "AppHost.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("LocalEnvironmentPolicy.SanitizeCurrentProcess()", source, StringComparison.Ordinal);
        Assert.Contains("AddPostgres(\"legacy-postgres-main\"", source, StringComparison.Ordinal);
        Assert.Contains("WithImageTag(\"18-alpine\")", source, StringComparison.Ordinal);
        Assert.Contains("LegacyTopology.DatabaseNames", source, StringComparison.Ordinal);
        Assert.Contains("postgres.AddDatabase", source, StringComparison.Ordinal);
        Assert.Contains("AddRedis(\"legacy-redis\"", source, StringComparison.Ordinal);
        Assert.Contains("WithImageTag(\"8.4-alpine\")", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__CountryDbContext", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__redis", source, StringComparison.Ordinal);
        Assert.Contains("Jwt__PublicKey", source, StringComparison.Ordinal);
        Assert.Contains("Jwt__Issuer", source, StringComparison.Ordinal);
        Assert.Contains("Jwt__Audience", source, StringComparison.Ordinal);
        Assert.Contains("/countries/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/countries/readiness", source, StringComparison.Ordinal);
        Assert.Contains("--memory", source, StringComparison.Ordinal);
        Assert.Contains("WaitFor(postgres)", source, StringComparison.Ordinal);
        Assert.Contains("WaitFor(redis)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gcloud", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kubectl", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argocd", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HttpLaunchProfile_ExplicitlyAllowsLocalUnsecuredTransport()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "Legacy.Maliev.AppHost",
            "Properties",
            "launchSettings.json");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("ASPIRE_ALLOW_UNSECURED_TRANSPORT", source, StringComparison.Ordinal);
        Assert.Contains("\"true\"", source, StringComparison.Ordinal);
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
