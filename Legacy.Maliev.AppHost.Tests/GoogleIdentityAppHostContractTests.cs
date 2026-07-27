namespace Legacy.Maliev.AppHost.Tests;

public sealed class GoogleIdentityAppHostContractTests
{
    [Fact]
    public void AppHost_ProjectsGoogleIdentityConfigurationToAuthAndIntranetBffWithoutHardcodedCredentials()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Legacy.Maliev.AppHost", "AppHost.cs"));
        var authStart = source.IndexOf("var auth =", StringComparison.Ordinal);
        var authEnd = source.IndexOf("var customerDatabase =", authStart, StringComparison.Ordinal);
        var bffStart = source.IndexOf("var intranetBff =", StringComparison.Ordinal);
        var bffEnd = source.IndexOf("builder.Build()", bffStart, StringComparison.Ordinal);

        Assert.True(authStart >= 0 && authEnd > authStart, "AuthService resource was not found.");
        Assert.True(bffStart >= 0 && bffEnd > bffStart, "Intranet BFF resource was not found.");

        var authResource = source[authStart..authEnd];
        var bffResource = source[bffStart..bffEnd];

        Assert.Contains("GoogleIdentity__Employee__HostedDomain", authResource, StringComparison.Ordinal);
        Assert.Contains("GoogleIdentity__Employee__Audiences__intranet", authResource, StringComparison.Ordinal);
        Assert.Contains("Authentication__Google__ClientId", bffResource, StringComparison.Ordinal);
        Assert.Contains("MALIEV_GOOGLE_IDENTITY_CLIENT_ID", source, StringComparison.Ordinal);
        Assert.Contains("MALIEV_GOOGLE_IDENTITY_HOSTED_DOMAIN", source, StringComparison.Ordinal);
        Assert.Contains("\"maliev.com\"", source, StringComparison.Ordinal);

        Assert.DoesNotContain("client_secret", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AIza", source, StringComparison.Ordinal);
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
