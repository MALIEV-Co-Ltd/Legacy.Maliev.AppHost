namespace Legacy.Maliev.AppHost.Tests;

public sealed class AppHostSourceContractTests
{
    [Fact]
    public void WebServiceIdentity_HasExactLeastPrivilegeMemberAddressPermissions()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Legacy.Maliev.AppHost", "AppHost.cs"));
        var permissions = System.Text.RegularExpressions.Regex.Matches(
                source,
                "ServiceClients__Clients__legacy-web__Permissions__\\d+\", \"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(
            [
                "legacy-auth.customer-self-service",
                "legacy-customer.customers.create",
                "legacy-customer.customers.delete",
                "legacy.notifications.send",
                "legacy-customer.customers.read",
                "legacy-customer.customers.update",
                "legacy-customer.addresses.create",
                "legacy-customer.addresses.update",
                "legacy-customer.companies.create",
                "legacy-customer.companies.update",
                "legacy-customer.companies.delete",
            ],
            permissions);
    }

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
        Assert.Contains("WaitFor(countryDatabase)", source, StringComparison.Ordinal);
        Assert.Contains("WaitFor(redis)", source, StringComparison.Ordinal);
        Assert.Contains("Legacy_Maliev_AppHost_MigrationRunner", source, StringComparison.Ordinal);
        Assert.Contains("WaitForCompletion", source, StringComparison.Ordinal);
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

    [Fact]
    public void AppHost_WiresTheStatelessDocumentServiceWithoutInfrastructureDependencies()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));

        Assert.Contains("Legacy_Maliev_DocumentService_Api", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-document-service", source, StringComparison.Ordinal);
        Assert.Contains("WithHttpEndpoint(name: \"http\")", source, StringComparison.Ordinal);
        Assert.Contains("WithEnvironment(\"ASPNETCORE_ENVIRONMENT\", \"Development\")", source, StringComparison.Ordinal);
        Assert.Contains("/documents/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/documents/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/documents/scalar", source, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.DocumentService", project, StringComparison.Ordinal);

        var documentStart = source.IndexOf("Legacy_Maliev_DocumentService_Api", StringComparison.Ordinal);
        var documentEnd = source.IndexOf("var authMigrations", documentStart, StringComparison.Ordinal);
        var documentResource = source[documentStart..documentEnd];
        Assert.Contains("Jwt__PublicKey", documentResource, StringComparison.Ordinal);
        Assert.Contains("Jwt__Issuer", documentResource, StringComparison.Ordinal);
        Assert.Contains("Jwt__Audience", documentResource, StringComparison.Ordinal);
        Assert.Contains("DOTNET_GCHeapHardLimit", documentResource, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__", documentResource, StringComparison.Ordinal);
        Assert.DoesNotContain(".WaitFor(", documentResource, StringComparison.Ordinal);
        Assert.DoesNotContain(".WaitForCompletion(", documentResource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_WiresThePublicCustomerAccountBoundaryWithoutProductionSecrets()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));

        Assert.Contains("Legacy_Maliev_AuthService_Api", source, StringComparison.Ordinal);
        Assert.Contains("Legacy_Maliev_CustomerService_Api", source, StringComparison.Ordinal);
        Assert.Contains("Legacy_Maliev_NotificationService_Api", source, StringComparison.Ordinal);
        Assert.Contains("Legacy_Maliev_Web", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-auth-service", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-customer-service", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-notification-service", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-web", source, StringComparison.Ordinal);

        Assert.Contains("ConnectionStrings__RefreshSessions", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__CustomerIdentity", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__EmployeeIdentity", source, StringComparison.Ordinal);
        Assert.Contains("IdentityStorage__Provider", source, StringComparison.Ordinal);
        Assert.Contains("\"PostgreSql\"", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__CustomerDbContext", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__redis", source, StringComparison.Ordinal);
        Assert.Contains("ServiceAuthentication__ClientId", source, StringComparison.Ordinal);
        Assert.Contains("ServiceAuthentication__ClientSecret", source, StringComparison.Ordinal);
        Assert.Contains("ServiceClients__Clients__legacy-web__SecretSha256", source, StringComparison.Ordinal);
        Assert.Contains("DataProtection__CertificatePfxBase64", source, StringComparison.Ordinal);
        Assert.Contains("DataProtection__CertificatePassword", source, StringComparison.Ordinal);
        Assert.Contains("Services__Auth", source, StringComparison.Ordinal);
        Assert.Contains("Services__Customer", source, StringComparison.Ordinal);
        Assert.Contains("Services__Notification", source, StringComparison.Ordinal);
        Assert.Contains("AuthService__LegacyCustomerIdentityBaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", source, StringComparison.Ordinal);
        Assert.Contains("Development", source, StringComparison.Ordinal);
        Assert.Contains("ConfigureDynamicHttpEndpoint", source, StringComparison.Ordinal);

        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.AuthService", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.CustomerService", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.NotificationService", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.Web", project, StringComparison.Ordinal);
        Assert.Equal(6, project.Split("AdditionalProperties=\"Configuration=$(Configuration)\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(6, project.Split("SetConfiguration=\"Configuration=$(Configuration)\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("maliev-legacy-secrets", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEGACY_DEPLOY_ENABLED", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppHost_UsesOneConsistentLocalJwtTrustContract()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Legacy.Maliev.AppHost", "AppHost.cs"));

        Assert.Contains("LegacyTopology.JwtIssuer", source, StringComparison.Ordinal);
        Assert.Contains("LegacyTopology.JwtAudience", source, StringComparison.Ordinal);
        Assert.Contains("Jwt__PrivateKeyPem", source, StringComparison.Ordinal);
        Assert.Contains("Jwt__KeyId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("https://legacy-iam.localhost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("maliev-legacy-services", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomerMigration_SeedsTheProfileOwnedByTheLocalCustomerIdentity()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Legacy.Maliev.AppHost.MigrationRunner",
            "Program.cs"));

        Assert.Contains("await SeedCustomerAsync(context);", source, StringComparison.Ordinal);
        Assert.Contains("LegacyTopology.LocalCustomerEmail", source, StringComparison.Ordinal);
        Assert.Contains("FirstName = \"Local\"", source, StringComparison.Ordinal);
        Assert.Contains("LastName = \"Customer\"", source, StringComparison.Ordinal);
        Assert.Contains("customer.Id != 1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationScript_PollsAndChecksTheLocalServiceTopology()
    {
        var sourcePath = Path.Combine(FindRepositoryRoot(), "scripts", "verify-local-stack.ps1");

        Assert.True(File.Exists(sourcePath), $"Expected verification script at {sourcePath}.");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("while (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/countries/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/countries/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/countries/scalar", source, StringComparison.Ordinal);
        Assert.Contains("/Countries", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-document-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/documents/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/documents/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/documents/scalar", source, StringComparison.Ordinal);
        Assert.Contains("/Pdfs/invoice", source, StringComparison.Ordinal);
        Assert.Contains("legacy-auth-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-customer-identity-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-employee-identity-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-customer-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-auth-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/auth/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/auth/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/auth/scalar", source, StringComparison.Ordinal);
        Assert.Contains("/auth/v1/login", source, StringComparison.Ordinal);
        Assert.Contains("local.customer@maliev.test", source, StringComparison.Ordinal);
        Assert.Contains("local.employee@maliev.test", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-customer-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/customer/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/customer/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/customer/scalar", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-notification-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/emails/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/emails/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/emails/scalar", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-web-*", source, StringComparison.Ordinal);
        Assert.Contains("/web/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/web/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/Account/Login", source, StringComparison.Ordinal);
        Assert.Contains("/Account/Signup", source, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = $false", source, StringComparison.Ordinal);
        Assert.Contains("Headers.GetValues('Set-Cookie')", source, StringComparison.Ordinal);
        Assert.Contains("TryAddWithoutValidation('Cookie', $antiforgeryCookie)", source, StringComparison.Ordinal);
        Assert.Contains("\"$antiforgeryCookie; $sessionCookie\"", source, StringComparison.Ordinal);
        Assert.Contains("__RequestVerificationToken", source, StringComparison.Ordinal);
        Assert.Contains("/member/account/manage/address", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BillingAddress1", source, StringComparison.Ordinal);
        Assert.Contains("/member/account/manage/profile", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CompanyName", source, StringComparison.Ordinal);
        Assert.Contains("/member/account/manage/changepassword", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CurrentPassword", source, StringComparison.Ordinal);
        Assert.Contains("NewPassword", source, StringComparison.Ordinal);
        Assert.Contains("'Auth'", source, StringComparison.Ordinal);
        Assert.Contains("dotnet build $appHostProject --configuration Release", source, StringComparison.Ordinal);
        Assert.Contains("-Method Post", source, StringComparison.Ordinal);
        var postHelperStart = source.IndexOf("function Invoke-ExpectedPostStatus", StringComparison.Ordinal);
        var postHelperEnd = source.IndexOf("function Get-SingleResource", postHelperStart, StringComparison.Ordinal);
        var postHelper = source[postHelperStart..postHelperEnd];
        Assert.Contains("[string]$Body = '{}'", postHelper, StringComparison.Ordinal);
        Assert.Contains("-Body $Body", postHelper, StringComparison.Ordinal);
        Assert.Contains("-ExpectedStatus 401", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--kubeconfig", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gcloud", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kubectl apply", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argocd", source, StringComparison.OrdinalIgnoreCase);
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
