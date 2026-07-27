namespace Legacy.Maliev.AppHost.Tests;

public sealed class AspireMigrationSeedContractTests
{
    [Fact]
    public void CareerMigration_DoesNotInventAJobOffer()
    {
        var source = ReadMigrationRunner();

        Assert.DoesNotContain("SeedCareerAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Local Manufacturing Engineer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotationCompatibilitySeed_UsesUnspecifiedDateTimeForLegacyTimestampColumns()
    {
        var source = ReadMigrationRunner();
        var start = source.IndexOf("static async Task SeedQuotationAsync", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected the compatibility quotation seed method.");

        var quotationSeed = source[start..];
        Assert.Contains("DateTimeKind.Unspecified", quotationSeed, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeKind.Utc", quotationSeed, StringComparison.Ordinal);
    }

    private static string ReadMigrationRunner() => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "Legacy.Maliev.AppHost.MigrationRunner",
        "Program.cs"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the Legacy.Maliev.AppHost repository root.");
    }
}
