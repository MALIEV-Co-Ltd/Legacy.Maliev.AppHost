namespace Legacy.Maliev.AppHost.Tests;

public sealed class GoogleMapsAppHostContractTests
{
    [Fact]
    public void AppHost_WiresBrowserMapsKeyOnlyToIntranetBff()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Legacy.Maliev.AppHost", "AppHost.cs"));
        var bffStart = source.IndexOf("var intranetBff", StringComparison.Ordinal);
        var compositionEnd = source.IndexOf("return builder;", bffStart, StringComparison.Ordinal);

        Assert.True(bffStart >= 0, "The AppHost must declare the legacy Intranet BFF resource.");
        Assert.True(compositionEnd > bffStart, "The Intranet BFF resource must appear before AppHost composition returns.");

        var bffResource = source[bffStart..compositionEnd];
        Assert.Contains("legacy-web-google-maps-embed-api-key", source, StringComparison.Ordinal);
        Assert.Contains("legacy-intranet-google-maps-browser-api-key", source, StringComparison.Ordinal);
        Assert.Contains("GoogleMaps__BrowserApiKey", bffResource, StringComparison.Ordinal);
        Assert.DoesNotContain("GoogleMaps__EmbedApiKey", bffResource, StringComparison.Ordinal);
        Assert.Contains("GoogleMaps__EmbedApiKey", source[..bffStart], StringComparison.Ordinal);
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
