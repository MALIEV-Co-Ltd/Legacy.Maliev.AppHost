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
                "legacy.customer-orders.read",
                "legacy.customer-orders.cancel",
                "legacy.customer-quotations.read",
                "legacy-contact.messages.create",
                "legacy.quotation-requests.create",
                "legacy.quotation-files.write",
                "legacy-file.uploads.create",
                "legacy-file.uploads.delete",
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
        Assert.Contains("Notifications__UseDevelopmentRecordingProvider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Brevo__ApiKey", source, StringComparison.Ordinal);
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
        Assert.Equal(17, project.Split("AdditionalProperties=\"Configuration=$(Configuration)\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(17, project.Split("SetConfiguration=\"Configuration=$(Configuration)\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("maliev-legacy-secrets", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEGACY_DEPLOY_ENABLED", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppHost_WiresCareerContactAndStandaloneAccountingBoundaries()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));
        var runnerProject = File.ReadAllText(Path.Combine(
            root,
            "Legacy.Maliev.AppHost.MigrationRunner",
            "Legacy.Maliev.AppHost.MigrationRunner.csproj"));
        var migrations = File.ReadAllText(Path.Combine(
            root,
            "Legacy.Maliev.AppHost.MigrationRunner",
            "Program.cs"));

        foreach (var service in new[] { "Career", "Contact", "Accounting" })
        {
            Assert.Contains($"Legacy_Maliev_{service}Service_Api", source, StringComparison.Ordinal);
            Assert.Contains($"$(MalievWorkspaceRoot)\\Legacy.Maliev.{service}Service", project, StringComparison.Ordinal);
            Assert.Contains($"$(MalievWorkspaceRoot)\\Legacy.Maliev.{service}Service", runnerProject, StringComparison.Ordinal);
        }

        Assert.Contains("Services__Career", source, StringComparison.Ordinal);
        Assert.Contains("Services__Contact", source, StringComparison.Ordinal);
        Assert.Contains("legacy-career-migrations", source, StringComparison.Ordinal);
        Assert.Contains("legacy-contact-migrations", source, StringComparison.Ordinal);
        Assert.Contains("legacy-payment-migrations", source, StringComparison.Ordinal);
        Assert.Contains("legacy-invoice-migrations", source, StringComparison.Ordinal);
        Assert.Contains("legacy-receipt-migrations", source, StringComparison.Ordinal);
        Assert.Contains("\"career\" => \"CareerDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"contact\" => \"ContactRequestDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"payment\" => \"PaymentDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"invoice\" => \"InvoiceDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"receipt\" => \"ReceiptDbContext\"", migrations, StringComparison.Ordinal);

        var accountingStart = source.IndexOf("Legacy_Maliev_AccountingService_Api", StringComparison.Ordinal);
        var accountingEnd = source.IndexOf("builder.AddProject<Projects.Legacy_Maliev_Web>", accountingStart, StringComparison.Ordinal);
        var accountingResource = source[accountingStart..accountingEnd];
        Assert.DoesNotContain("Services__Accounting", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceAuthentication__", accountingResource, StringComparison.Ordinal);
        Assert.Contains("Jwt__PublicKey", accountingResource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_WiresTheCustomerOwnedOrderBoundaryWithSeparateLegacyDatabases()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));
        var migrations = File.ReadAllText(Path.Combine(
            root,
            "Legacy.Maliev.AppHost.MigrationRunner",
            "Program.cs"));

        Assert.Contains("Legacy_Maliev_OrderService_Api", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-order-service", source, StringComparison.Ordinal);
        Assert.Contains("legacy-order-migrations", source, StringComparison.Ordinal);
        Assert.Contains("legacy-order-status-migrations", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__OrderDbContext", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__OrderStatusDbContext", source, StringComparison.Ordinal);
        Assert.Contains("Services__Order", source, StringComparison.Ordinal);
        Assert.Contains("/order/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/order/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/order/scalar", source, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.OrderService", project, StringComparison.Ordinal);
        Assert.Contains("\"order\" => \"OrderDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"order-status\" => \"OrderStatusDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("await SeedOrderAsync(context);", migrations, StringComparison.Ordinal);
        Assert.Contains("await SeedOrderStatusesAsync(context);", migrations, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_WiresTheLegacyIntranetAsAnIndependentServiceBoundary()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));
        var migrations = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost.MigrationRunner", "Program.cs"));

        Assert.Contains("Legacy_Maliev_Intranet", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-intranet", source, StringComparison.Ordinal);
        Assert.Contains("ServiceClients__Clients__legacy-intranet__SecretSha256", source, StringComparison.Ordinal);
        Assert.Contains("LegacyTopology.IntranetPermissions", source, StringComparison.Ordinal);
        Assert.Contains("ServiceAuthentication__ClientId", source, StringComparison.Ordinal);
        Assert.Contains("\"legacy-intranet\"", source, StringComparison.Ordinal);
        Assert.Contains("ServiceAuthentication__ClientSecret", source, StringComparison.Ordinal);
        Assert.Contains("Services__Catalog", source, StringComparison.Ordinal);
        Assert.Contains("Services__Employee", source, StringComparison.Ordinal);
        Assert.Contains("Services__Procurement", source, StringComparison.Ordinal);
        Assert.Contains("Services__File", source, StringComparison.Ordinal);
        Assert.Contains("/intranet/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/intranet/readiness", source, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.Intranet", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.CatalogService", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.EmployeeService", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.ProcurementService", project, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.FileService", project, StringComparison.Ordinal);
        Assert.Contains("\"catalog\" => \"CatalogDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"employee\" => \"EmployeeDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"supplier\" => \"SupplierDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"purchase-order\" => \"PurchaseOrderDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"file\" => \"FileDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("ServiceClients__Clients__legacy-intranet__Permissions__{permissionIndex}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceClients__Clients__legacy-intranet__Permissions__0\", \"*\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_RunsCompatibilityAndBffWithTheSamePublicJwtTrust()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));

        Assert.Contains(
            "Legacy.Maliev.Intranet\\Legacy.Maliev.Intranet.Bff\\Legacy.Maliev.Intranet.Bff.csproj",
            project,
            StringComparison.Ordinal);

        var compatibility = ExtractResource(
            source,
            "var intranetCompatibility = builder.AddProject<Projects.Legacy_Maliev_Intranet>",
            "var intranetBff = builder.AddProject<Projects.Legacy_Maliev_Intranet_Bff>");
        var bff = ExtractResource(
            source,
            "var intranetBff = builder.AddProject<Projects.Legacy_Maliev_Intranet_Bff>",
            "builder.Build().Run()");

        Assert.Contains("\"legacy-maliev-intranet\"", compatibility, StringComparison.Ordinal);
        Assert.Contains("\"legacy-maliev-intranet-bff\"", bff, StringComparison.Ordinal);
        foreach (var resource in new[] { compatibility, bff })
        {
            Assert.Contains("WithEnvironment(\"Jwt__PublicKey\", jwt.PublicKeyBase64)", resource, StringComparison.Ordinal);
            Assert.Contains("WithEnvironment(\"Jwt__Issuer\", LegacyTopology.JwtIssuer)", resource, StringComparison.Ordinal);
            Assert.Contains("WithEnvironment(\"Jwt__Audience\", LegacyTopology.JwtAudience)", resource, StringComparison.Ordinal);
            Assert.Contains("WithEnvironment(\"Jwt__KeyId\", LegacyTopology.JwtKeyId)", resource, StringComparison.Ordinal);
            Assert.Contains("WithEnvironment(\"ConnectionStrings__redis\", redis.Resource.ConnectionStringExpression)", resource, StringComparison.Ordinal);
            Assert.Contains("WithEnvironment(\"Services__Auth\", auth.GetEndpoint(\"http\"))", resource, StringComparison.Ordinal);
            Assert.Contains(".WithReference(redis)", resource, StringComparison.Ordinal);
            Assert.Contains(".WithReference(auth)", resource, StringComparison.Ordinal);
            Assert.Contains(".WaitFor(redis)", resource, StringComparison.Ordinal);
            Assert.Contains(".WaitFor(auth)", resource, StringComparison.Ordinal);
            Assert.DoesNotContain("Jwt__PrivateKeyPem", resource, StringComparison.Ordinal);
        }

        Assert.Contains("/intranet/liveness", compatibility, StringComparison.Ordinal);
        Assert.Contains("/intranet-bff/liveness", bff, StringComparison.Ordinal);
    }

    [Fact]
    public void AppHost_WiresCustomerOwnedQuotationReadsWithPreservedDatabases()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "AppHost.cs"));
        var project = File.ReadAllText(Path.Combine(root, "Legacy.Maliev.AppHost", "Legacy.Maliev.AppHost.csproj"));
        var migrations = File.ReadAllText(Path.Combine(
            root,
            "Legacy.Maliev.AppHost.MigrationRunner",
            "Program.cs"));

        Assert.Contains("Legacy_Maliev_QuotationService_Api", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-quotation-service", source, StringComparison.Ordinal);
        Assert.Contains("legacy-quotation-migrations", source, StringComparison.Ordinal);
        Assert.Contains("legacy-quotation-request-migrations", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__QuotationDbContext", source, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__QuotationRequestDbContext", source, StringComparison.Ordinal);
        Assert.Contains("Services__Quotation", source, StringComparison.Ordinal);
        Assert.Contains("/quotation/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/quotation/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/quotation/scalar", source, StringComparison.Ordinal);
        Assert.Contains("$(MalievWorkspaceRoot)\\Legacy.Maliev.QuotationService", project, StringComparison.Ordinal);
        Assert.Contains("\"quotation\" => \"QuotationDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("\"quotation-request\" => \"QuotationRequestDbContext\"", migrations, StringComparison.Ordinal);
        Assert.Contains("await SeedQuotationAsync(context);", migrations, StringComparison.Ordinal);
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
        Assert.Contains("/InstantQuotation/3D-Printing?culture=en", source, StringComparison.Ordinal);
        Assert.Contains("handler=GetEstimate", source, StringComparison.Ordinal);
        Assert.Contains("Get an instant manufacturing estimate", source, StringComparison.Ordinal);
        Assert.Contains("currency -ne 'THB'", source, StringComparison.Ordinal);
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
        Assert.Contains("/member/account/manage/changeemail", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CurrentPassword", source, StringComparison.Ordinal);
        Assert.Contains("NewPassword", source, StringComparison.Ordinal);
        Assert.Contains("NewEmail", source, StringComparison.Ordinal);
        Assert.Contains("/notifications/development/recorded", source, StringComparison.Ordinal);
        Assert.Contains("local.changed@maliev.test", source, StringComparison.Ordinal);
        Assert.Contains("legacy-order-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-order-status-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-order-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/order/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/order/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/order/scalar", source, StringComparison.Ordinal);
        Assert.Contains("/member/orders/history", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/member/orders/view?itemID=1", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/member/orders/3d-printing", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/member/orders/3d-scanning", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/member/orders/cnc-machining", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExpectedItem = '3D-Printing'", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedItem = '3D-Scanning'", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedItem = 'CNC-Machining'", source, StringComparison.Ordinal);
        Assert.Contains("AbsolutePath -notin '/Quotation', '/Quotation/Index'", source, StringComparison.Ordinal);
        Assert.Contains("Headers.Location", source, StringComparison.Ordinal);
        Assert.Contains("handler=CancelOrder", source, StringComparison.Ordinal);
        Assert.Contains("orderId", source, StringComparison.Ordinal);
        Assert.Contains("legacy-catalog-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-employee-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-supplier-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-purchase-order-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-file-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-catalog-service-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-employee-service-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-procurement-service-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-file-service-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-intranet-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-career-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-contact-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-payment-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-invoice-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-receipt-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-career-service-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-contact-service-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-accounting-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/career?culture=en", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/contact?culture=en", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/accounting/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/accounting/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/accounting/scalar", source, StringComparison.Ordinal);
        Assert.Contains("/payments", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-IntranetEmployeeFlow", source, StringComparison.Ordinal);
        Assert.Contains("/Customers/Index", source, StringComparison.Ordinal);
        Assert.Contains("/Employees/Index", source, StringComparison.Ordinal);
        Assert.Contains("/Materials/Index", source, StringComparison.Ordinal);
        Assert.Contains("/Suppliers/Index", source, StringComparison.Ordinal);
        Assert.Contains("/Orders/Index", source, StringComparison.Ordinal);
        Assert.Contains("/PurchaseOrders/Index", source, StringComparison.Ordinal);
        Assert.Contains("legacy-quotation-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-quotation-request-migrations-*", source, StringComparison.Ordinal);
        Assert.Contains("legacy-maliev-quotation-service-*", source, StringComparison.Ordinal);
        Assert.Contains("/quotation/liveness", source, StringComparison.Ordinal);
        Assert.Contains("/quotation/readiness", source, StringComparison.Ordinal);
        Assert.Contains("/quotation/scalar", source, StringComparison.Ordinal);
        Assert.Contains("/member/quotations/index", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/member/quotations/view?id=1", source, StringComparison.OrdinalIgnoreCase);
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

    private static string ExtractResource(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected resource marker '{startMarker}'.");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected resource terminator '{endMarker}'.");
        return source[start..end];
    }
}
