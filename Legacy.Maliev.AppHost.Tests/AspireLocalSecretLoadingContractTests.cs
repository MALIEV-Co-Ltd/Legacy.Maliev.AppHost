namespace Legacy.Maliev.AppHost.Tests;

public sealed class AspireLocalSecretLoadingContractTests
{
    [Fact]
    public void CurrentWebLauncher_LoadsOnlyTheWorkspaceGoogleMapsKeyWithoutPrintingIt()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "start-current-web.ps1"));

        Assert.Contains("Parameters__legacy-google-maps-api-key", source, StringComparison.Ordinal);
        Assert.Contains("Maliev.Aspire\\Maliev.Aspire.AppHost\\sharedsecrets.json", source, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-Json", source, StringComparison.Ordinal);
        Assert.Contains(".GoogleMaps.BrowserApiKey", source, StringComparison.Ordinal);
        Assert.Contains("Environment]::SetEnvironmentVariable($googleMapsParameterName, $googleMapsApiKey)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $googleMapsApiKey", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BrowserApiKey =", source, StringComparison.Ordinal);
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
