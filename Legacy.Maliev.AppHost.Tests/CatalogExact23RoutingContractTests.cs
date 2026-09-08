namespace Legacy.Maliev.AppHost.Tests;

public sealed class CatalogExact23RoutingContractTests
{
    [Fact]
    public void CatalogUsesEachRetainedDatabaseWithoutSnapshotComposition()
    {
        var root = FindRepositoryRoot();
        var appHost = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var runner = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost.MigrationRunner", "Program.cs"));

        Assert.Contains(
            ".WithEnvironment(\"ConnectionStrings__CatalogDbContext\", CreatePooledDatabaseConnectionString(\"Material\"))",
            appHost,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"ConnectionStrings__CountryDbContext\", CreatePooledDatabaseConnectionString(\"Country\"))",
            appHost,
            StringComparison.Ordinal);
        Assert.Contains(
            ".WithEnvironment(\"ConnectionStrings__CurrencyDbContext\", CreatePooledDatabaseConnectionString(\"Currency\"))",
            appHost,
            StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-snapshot-compose", appHost, StringComparison.Ordinal);
        Assert.DoesNotContain("catalog-snapshot-compose", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("TRUNCATE TABLE \"Currency\"", runner, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the Legacy.Maliev.AppHost repository root.");
    }
}
