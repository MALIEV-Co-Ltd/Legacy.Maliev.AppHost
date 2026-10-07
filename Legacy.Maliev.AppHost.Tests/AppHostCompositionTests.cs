using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Legacy.Maliev.AppHost.Topology;
using Microsoft.Extensions.Configuration;

namespace Legacy.Maliev.AppHost.Tests;

[CollectionDefinition("AppHostComposition", DisableParallelization = true)]
public sealed class AppHostCompositionCollection;

[Collection("AppHostComposition")]
public sealed class AppHostCompositionTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task CatalogLookupReadsAreEmittedOnlyForExistingServerWorkloads(string environmentName)
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose(environmentName: environmentName);
        using var application = builder.Build();
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");

        var webPrefix = "ServiceClients__Clients__legacy-web__Permissions__";
        Assert.Equal(31, auth.Keys.Count(key => key.StartsWith(webPrefix, StringComparison.Ordinal)));
        Assert.Equal("legacy-customer.addresses.update", auth[webPrefix + "7"]);
        Assert.Equal("legacy.orders.delete", auth[webPrefix + "27"]);
        Assert.Equal("legacy-catalog.locations.read", auth[webPrefix + "28"]);
        Assert.Equal("legacy-catalog.companies.read", auth[webPrefix + "29"]);

        var intranetPrefix = "ServiceClients__Clients__legacy-intranet__Permissions__";
        Assert.Equal(LegacyTopology.IntranetPermissions.Count + 2,
            auth.Keys.Count(key => key.StartsWith(intranetPrefix, StringComparison.Ordinal)));
        for (var index = 0; index < LegacyTopology.IntranetPermissions.Count; index++)
        {
            Assert.Equal(LegacyTopology.IntranetPermissions[index], auth[intranetPrefix + index]);
        }
        Assert.Equal("legacy-catalog.locations.read", auth[intranetPrefix + LegacyTopology.IntranetPermissions.Count]);
        Assert.Equal("legacy-catalog.companies.read", auth[intranetPrefix + (LegacyTopology.IntranetPermissions.Count + 1)]);

        foreach (var permission in new[] { "legacy-catalog.locations.read", "legacy-catalog.companies.read" })
        {
            var recipients = auth.Where(entry => entry.Key.Contains("__Permissions__", StringComparison.Ordinal)
                && entry.Value is string value && value == permission).Select(entry => entry.Key).ToArray();
            Assert.Equal(2, recipients.Length);
            Assert.All(recipients, key => Assert.True(key.StartsWith(webPrefix, StringComparison.Ordinal)
                || key.StartsWith(intranetPrefix, StringComparison.Ordinal), key));
        }

        foreach (var (resourceName, clientId) in new[]
        {
            ("legacy-maliev-web", "legacy-web"),
            ("legacy-maliev-intranet-bff", "legacy-intranet"),
        })
        {
            var workload = await EnvironmentFor(builder, resourceName);
            Assert.Equal(clientId, workload["ServiceAuthentication__ClientId"]);
            var catalogEndpoint = Assert.IsType<EndpointReference>(workload["Services__Catalog"]);
            Assert.Equal("legacy-maliev-catalog-service", catalogEndpoint.Resource.Name);
            Assert.Equal("http", catalogEndpoint.EndpointName);
            Assert.DoesNotContain(workload.Keys, key => key.StartsWith("ServiceClients__Clients__", StringComparison.Ordinal));
        }
        // Evaluate configuration only: no application.Start/Run, containers, tokens or provider calls.
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task WebSignedFileReadEnrollmentPreservesOtherClientsAndBindsTheFileEndpoint(string environmentName)
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose(environmentName: environmentName);
        using var application = builder.Build();
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");
        var web = await EnvironmentFor(builder, "legacy-maliev-web");

        string[] expectedWebPermissions =
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
            "legacy-catalog.countries.read",
            "legacy-catalog.currencies.read",
            "legacy-catalog.materials.read",
            "legacy-catalog.material-groups.read",
            "legacy.orders.create",
            "legacy.order-catalog.read",
            "legacy.order-files.write",
            "legacy.order-status.write",
            "legacy.orders.delete",
            "legacy-catalog.locations.read",
            "legacy-catalog.companies.read",
            "legacy-file.uploads.read",
        ];
        Assert.Equal(expectedWebPermissions, PermissionsFor("legacy-web"));
        Assert.Equal(LegacyCatalogLookupWorkloadGrants.AppendTo(LegacyTopology.IntranetPermissions), PermissionsFor("legacy-intranet"));
        Assert.Equal(LegacyTopology.AccountingPermissions, PermissionsFor("legacy-accounting"));
        Assert.Equal(["legacy.order-status.write"], PermissionsFor("legacy-quotation"));
        Assert.All(auth.Where(entry => entry.Key.Contains("__Permissions__", StringComparison.Ordinal)), entry =>
            Assert.DoesNotContain("*", Assert.IsType<string>(entry.Value), StringComparison.Ordinal));
        Assert.Equal(
            ["legacy-accounting", "legacy-intranet", "legacy-quotation", "legacy-web"],
            auth.Keys.Where(key => key.Contains("__Permissions__", StringComparison.Ordinal))
                .Select(key => key.Split("__", StringSplitOptions.None)[2]).Distinct().Order(StringComparer.Ordinal));

        Assert.Equal("legacy-web", web["ServiceAuthentication__ClientId"]);
        var secret = Assert.IsType<string>(web["ServiceAuthentication__ClientSecret"]);
        var enrolledHash = Assert.IsType<string>(auth["ServiceClients__Clients__legacy-web__SecretSha256"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))), enrolledHash.ToUpperInvariant());
        var endpoint = Assert.IsType<EndpointReference>(web["Services__File"]);
        Assert.Same(Assert.Single(builder.Resources, resource => resource.Name == "legacy-maliev-file-service"), endpoint.Resource);
        Assert.Equal("http", endpoint.EndpointName);
        Assert.DoesNotContain(web.Keys, key => key.StartsWith("ServiceClients__Clients__", StringComparison.Ordinal));

        string[] PermissionsFor(string clientId)
        {
            string prefix = $"ServiceClients__Clients__{clientId}__Permissions__";
            var entries = auth.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(entry => int.Parse(entry.Key[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(Enumerable.Range(0, entries.Length).Select(index => prefix + index), entries.Select(entry => entry.Key));
            return entries.Select(entry => Assert.IsType<string>(entry.Value)).ToArray();
        }
    }

    [Fact]
    public void ConfigurationUsesOnlyOwnedSyntheticProviderAndAllFiveParameters()
    {
        using var fixture = new GraphFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "appsettings.json"),
            "{\"Parameters\":{\"legacy-postgres-password\":\"poisoned-owned-json\"},\"OperatorSentinel\":\"poisoned-owned-json\"}");
        string ownedSecretsDirectory = Path.Combine(fixture.Root, "appdata", "Microsoft", "UserSecrets", "legacy-maliev-apphost");
        Directory.CreateDirectory(ownedSecretsDirectory);
        File.WriteAllText(Path.Combine(ownedSecretsDirectory, "secrets.json"),
            "{\"Parameters:legacy-postgres-password\":\"poisoned-owned-user-secret\",\"OperatorSentinel\":\"poisoned-owned-user-secret\"}");
        fixture.Set("ASPIRE_OPERATOR_SENTINEL", "must-not-enter-builder");
        fixture.Set("Parameters__legacy-postgres-password", "injected-operator-sentinel");
        var builder = fixture.Compose();
        using var application = builder.Build();
        Assert.Single(builder.Configuration.Sources);
        Assert.IsType<Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource>(builder.Configuration.Sources[0]);
        Assert.Equal(fixture.PostgresPassword, builder.Configuration["Parameters:legacy-postgres-password"]);
        Assert.Equal("owned-fixture-maps-embed", builder.Configuration["Parameters:legacy-web-google-maps-embed-api-key"]);
        Assert.Equal("owned-fixture-maps-browser", builder.Configuration["Parameters:legacy-intranet-google-maps-browser-api-key"]);
        Assert.Equal(5, builder.Resources.OfType<ParameterResource>().Count());
        Assert.Null(builder.Configuration["ASPIRE_OPERATOR_SENTINEL"]);
        Assert.Null(builder.Configuration["OperatorSentinel"]);
        Assert.Equal(fixture.Root, builder.AppHostDirectory);
    }

    [Fact]
    public void OrdinaryGraph_BuildsRealProjectAndDatabaseInventoryWithoutRunning()
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        Assert.Equal(LegacyTopology.DatabaseNames.Append("Auth").Order(StringComparer.Ordinal),
            builder.Resources.OfType<PostgresDatabaseResource>().Select(resource => resource.DatabaseName).Order(StringComparer.Ordinal));
        foreach (string name in new[] { "legacy-maliev-auth-service", "legacy-maliev-web", "legacy-maliev-intranet-bff" })
        {
            Assert.IsType<ProjectResource>(Assert.Single(builder.Resources, resource => resource.Name == name));
        }
        string[] expectedProjects =
        [
            "legacy-maliev-country-service", "legacy-maliev-document-service", "legacy-maliev-auth-service",
            "legacy-maliev-customer-service", "legacy-maliev-employee-service", "legacy-maliev-catalog-service",
            "legacy-maliev-procurement-service", "legacy-maliev-file-service", "legacy-maliev-notification-service",
            "legacy-maliev-order-service", "legacy-maliev-quotation-service", "legacy-maliev-career-service",
            "legacy-maliev-contact-service", "legacy-maliev-accounting-service", "legacy-maliev-web", "legacy-maliev-intranet-bff"
        ];
        Assert.Equal(expectedProjects.Order(StringComparer.Ordinal), builder.Resources.OfType<ProjectResource>()
            .Where(resource => resource.Name.StartsWith("legacy-maliev-", StringComparison.Ordinal))
            .Select(resource => resource.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(builder.Resources, resource => resource.Name == "legacy-maliev-intranet");
        Assert.DoesNotContain(builder.Resources, resource => resource.Name.Contains("prediction", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(builder.Resources.OfType<PostgresDatabaseResource>(), resource => resource.DatabaseName == "Log");
        Assert.True(File.Exists(Path.Combine(fixture.CertificateDirectory, "Web.json")));
        Assert.True(File.Exists(Path.Combine(fixture.CertificateDirectory, "Intranet.json")));
        // No application.Run/Start: construction is not runtime/data parity.
    }

    [Fact]
    public async Task EveryProjectReceivesItsIntendedGcHeapByteBudget()
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        var expectedMebibytes = new Dictionary<string, ulong>
        {
            ["legacy-maliev-country-service"] = 128,
            ["legacy-maliev-document-service"] = 192,
            ["legacy-maliev-auth-service"] = 128,
            ["legacy-maliev-customer-service"] = 128,
            ["legacy-maliev-employee-service"] = 128,
            ["legacy-maliev-catalog-service"] = 128,
            ["legacy-maliev-procurement-service"] = 128,
            ["legacy-maliev-file-service"] = 128,
            ["legacy-maliev-notification-service"] = 96,
            ["legacy-maliev-order-service"] = 128,
            ["legacy-maliev-quotation-service"] = 128,
            ["legacy-maliev-career-service"] = 128,
            ["legacy-maliev-contact-service"] = 128,
            ["legacy-maliev-accounting-service"] = 128,
            ["legacy-maliev-web"] = 192,
            ["legacy-maliev-intranet-bff"] = 192,
        };
        var projects = builder.Resources.OfType<ProjectResource>().ToArray();
        var applications = projects.Where(resource => resource.Name.StartsWith("legacy-maliev-", StringComparison.Ordinal)).ToArray();
        var migrations = projects.Except(applications).ToArray();
        Assert.NotEmpty(migrations);
        Assert.All(migrations, resource => Assert.EndsWith("-migrations", resource.Name, StringComparison.Ordinal));
        Assert.Equal(expectedMebibytes.Keys.Order(StringComparer.Ordinal),
            applications.Select(resource => resource.Name).Order(StringComparer.Ordinal));
        foreach (var (name, mebibytes) in expectedMebibytes)
        {
            string configured = Assert.IsType<string>((await EnvironmentFor(builder, name))["DOTNET_GCHeapHardLimit"]);
            // .NET interprets GC environment limits as hexadecimal, with an optional 0x prefix.
            string hexadecimal = configured.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? configured[2..] : configured;
            ulong bytes = ulong.Parse(hexadecimal, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(mebibytes * 1024 * 1024, bytes);
        }
        foreach (var migration in migrations)
        {
            Assert.DoesNotContain("DOTNET_GCHeapHardLimit", (await EnvironmentFor(builder, migration.Name)).Keys);
        }
    }

    [Fact]
    public async Task CountryRuntimeUsesPoolerWhileMigrationUsesDirectDatabase()
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        var service = await EnvironmentFor(builder, "legacy-maliev-country-service");
        var migration = await EnvironmentFor(builder, "legacy-country-migrations");
        string runtimeExpression = Assert.IsType<ReferenceExpression>(service["ConnectionStrings__CountryDbContext"]).ValueExpression;
        string migrationExpression = Assert.IsType<ReferenceExpression>(migration["ConnectionStrings__CountryDbContext"]).ValueExpression;
        Assert.Contains("legacy-postgres-pooler-rw", runtimeExpression, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-postgres-pooler-rw", migrationExpression, StringComparison.Ordinal);
        Assert.Equal("{legacy-postgres-main.connectionString};Database=Country", migrationExpression);
        Assert.Equal("false", migration["LEGACY_SKIP_MIGRATE"]);
    }

    [Fact]
    public async Task AdditionalDatabaseMappingsUseExactPooledStoresAndAuthRemainsDirect()
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        foreach ((string resource, string key, string database) in new[]
        {
            ("legacy-maliev-catalog-service", "ConnectionStrings__CatalogDbContext", "Material"),
            ("legacy-maliev-catalog-service", "ConnectionStrings__CountryDbContext", "Country"),
            ("legacy-maliev-catalog-service", "ConnectionStrings__CurrencyDbContext", "Currency"),
            ("legacy-maliev-career-service", "ConnectionStrings__CareerDbContext", "JobOffers"),
            ("legacy-maliev-contact-service", "ConnectionStrings__ContactRequestDbContext", "Message"),
            ("legacy-maliev-file-service", "ConnectionStrings__FileDbContext", "Upload")
        })
        {
            string expression = Assert.IsType<ReferenceExpression>((await EnvironmentFor(builder, resource))[key]).ValueExpression;
            Assert.Contains("legacy-postgres-pooler-rw", expression, StringComparison.Ordinal);
            Assert.Contains($"Database={database}", expression, StringComparison.Ordinal);
        }
        string authExpression = Assert.IsType<ReferenceExpression>((await EnvironmentFor(builder,
            "legacy-maliev-auth-service"))["ConnectionStrings__RefreshSessions"]).ValueExpression;
        Assert.DoesNotContain("legacy-postgres-pooler-rw", authExpression, StringComparison.Ordinal);
        Assert.Equal("{legacy-postgres-main.connectionString};Database=Auth", authExpression);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task CountryWorkloadDefaultDoesNotRegisterCallerOrInventIamResource(string? enabled)
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose(new Dictionary<string, string?> { ["CountryWorkload:Enabled"] = enabled });
        using var application = builder.Build();
        var country = await EnvironmentFor(builder, "legacy-maliev-country-service");
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");
        Assert.DoesNotContain(country.Keys, key => key.StartsWith("ServiceAuthentication__", StringComparison.Ordinal)
            || key.StartsWith("Services__Auth", StringComparison.Ordinal) || key.StartsWith("Services__IAM", StringComparison.Ordinal));
        Assert.DoesNotContain(auth.Keys, key => key.StartsWith("ServiceClients__Clients__owned-country__", StringComparison.Ordinal));
        Assert.DoesNotContain(builder.Resources, resource => resource.Name.Contains("iam", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("https://owned-iam.invalid", "Development")]
    [InlineData("https+http://IAMService", "Development")]
    [InlineData("http://127.0.0.1:14001", "Development")]
    [InlineData("http://127.0.0.1:14001", "Testing")]
    public async Task CountryWorkloadOptInProjectsExactOptionsHashEndpointAndDeclaredGrants(string origin, string environmentName)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        configuration["CountryWorkload:IamOrigin"] = origin;
        var builder = fixture.Compose(configuration, environmentName);
        using var application = builder.Build();
        var country = await EnvironmentFor(builder, "legacy-maliev-country-service");
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");
        var authResource = Assert.Single(builder.Resources, resource => resource.Name == "legacy-maliev-auth-service");
        Assert.Equal(environmentName, builder.Environment.EnvironmentName);
        Assert.Equal(environmentName, country["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("owned-country", country["ServiceAuthentication__ClientId"]);
        var secret = Assert.IsType<string>(country["ServiceAuthentication__ClientSecret"]);
        Assert.Equal(configuration["CountryWorkload:ClientSecret"], secret);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            auth["ServiceClients__Clients__owned-country__SecretSha256"]);
        var endpoint = Assert.IsType<EndpointReference>(country["Services__Auth__BaseUrl"]);
        Assert.Same(authResource, endpoint.Resource);
        Assert.Equal("http", endpoint.EndpointName);
        Assert.Equal(origin, country["Services__IAMService__BaseUrl"]);
        Assert.Equal(new[] { "ServiceClients__Clients__owned-country__Permissions__0", "ServiceClients__Clients__owned-country__Permissions__1" },
            auth.Keys.Where(key => key.StartsWith("ServiceClients__Clients__owned-country__Permissions__", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal("legacy-country.countries.read", auth["ServiceClients__Clients__owned-country__Permissions__0"]);
        Assert.Equal("legacy-country.countries.update", auth["ServiceClients__Clients__owned-country__Permissions__1"]);
        var resource = Assert.Single(builder.Resources, item => item.Name == "legacy-maliev-country-service");
        Assert.Contains(resource.Annotations.OfType<WaitAnnotation>(), wait => ReferenceEquals(wait.Resource, authResource));
        Assert.DoesNotContain(builder.Resources, item => item.Name.Contains("iam", StringComparison.OrdinalIgnoreCase));
        // Real graph construction only: no application Start/Run or IAM enrollment/authorization claim.
    }

    [Theory]
    [InlineData("Enabled", "maybe")]
    [InlineData("ClientId", null)]
    [InlineData("ClientId", "legacy-web")]
    [InlineData("ClientId", "owned:country")]
    [InlineData("ClientSecret", null)]
    [InlineData("ClientSecret", "too-short")]
    [InlineData("SecretSha256", null)]
    [InlineData("SecretSha256", "not-a-hash")]
    [InlineData("SecretSha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("IamOrigin", null)]
    [InlineData("IamOrigin", "http://remote.invalid")]
    [InlineData("IamOrigin", "https://owned-iam.invalid/iam")]
    [InlineData("IamOrigin", "https://user:password@owned-iam.invalid")]
    [InlineData("IamOrigin", "https://owned-iam.invalid?secret=value")]
    [InlineData("IamOrigin", "https+http://unregistered-iam")]
    [InlineData("Permissions:0", "*")]
    [InlineData("Permissions:0", "legacy-catalog.countries.read")]
    [InlineData("Permissions:0", "legacy-country.countries.update")]
    [InlineData("Permissions:0", null)]
    public void CountryWorkloadInvalidOptInFailsBeforeCertificateCreationWithoutDisclosingConfiguration(string key, string? value)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        configuration[$"CountryWorkload:{key}"] = value;
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Compose(configuration, "Development"));
        Assert.Equal("CountryWorkload opt-in requires an explicit valid identity, matching credential hash, IAM origin and exact Country permissions.", error.Message);
        Assert.DoesNotContain(configuration["CountryWorkload:ClientSecret"] ?? "not-present", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(1024)]
    public async Task CountryWorkloadPreservesExactSecretBytesAtBothAuthBounds(int length)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        var secret = " " + new string('x', length - 2) + " ";
        configuration["CountryWorkload:ClientSecret"] = secret;
        configuration["CountryWorkload:SecretSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToUpperInvariant();
        var builder = fixture.Compose(configuration, "Development");
        using var application = builder.Build();
        Assert.Equal(secret, (await EnvironmentFor(builder, "legacy-maliev-country-service"))["ServiceAuthentication__ClientSecret"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            (await EnvironmentFor(builder, "legacy-maliev-auth-service"))["ServiceClients__Clients__owned-country__SecretSha256"]);
    }

    [Fact]
    public async Task CountryWorkloadEnvironmentOptInSurvivesSanitizationWithoutAmbientCredentialInheritance()
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        foreach (var entry in configuration) fixture.Set(entry.Key.Replace(":", "__", StringComparison.Ordinal), entry.Value);
        fixture.Set("UNRELATED_MACHINE_VARIABLE", "synthetic-unrelated-value");
        var builder = fixture.Compose(environmentName: "Development", preserveCountryEnvironment: true);
        using var application = builder.Build();
        var country = await EnvironmentFor(builder, "legacy-maliev-country-service");
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");
        Assert.Equal(configuration["CountryWorkload:ClientSecret"], country["ServiceAuthentication__ClientSecret"]);
        Assert.Equal(configuration["CountryWorkload:SecretSha256"], auth["ServiceClients__Clients__owned-country__SecretSha256"]);
        Assert.Equal(configuration["CountryWorkload:IamOrigin"], country["Services__IAMService__BaseUrl"]);
        foreach (var key in configuration.Keys)
        {
            Assert.Null(Environment.GetEnvironmentVariable(key.Replace(":", "__", StringComparison.Ordinal)));
        }
        Assert.Null(Environment.GetEnvironmentVariable("UNRELATED_MACHINE_VARIABLE"));
        Assert.DoesNotContain(country.Keys, key => key.StartsWith("CountryWorkload__", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(auth.Keys, key => key.StartsWith("CountryWorkload__", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SecretSha256", null)]
    [InlineData("Permissions:4", "legacy-country.countries.read")]
    public void CountryWorkloadEnvironmentInvalidOptInFailsBeforeCertificateCreation(string key, string? value)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        configuration["CountryWorkload:" + key] = value;
        foreach (var entry in configuration) fixture.Set(entry.Key.Replace(":", "__", StringComparison.Ordinal), entry.Value);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Compose(
            environmentName: "Development", preserveCountryEnvironment: true));
        Assert.DoesNotContain(configuration["CountryWorkload:ClientSecret"]!, error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Fact]
    public void CountryWorkloadEnvironmentCannotActivateInProduction()
    {
        using var fixture = new GraphFixture();
        foreach (var entry in CountryWorkloadFixtureConfiguration()) fixture.Set(entry.Key.Replace(":", "__", StringComparison.Ordinal), entry.Value);
        Assert.Throws<InvalidOperationException>(() => fixture.Compose(
            environmentName: "Production", preserveCountryEnvironment: true));
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task CountryWorkloadEnvironmentAbsentOrDisabledPreservesDefaultGraph(string? enabled)
    {
        using var fixture = new GraphFixture();
        fixture.Set("CountryWorkload__Enabled", enabled);
        var builder = fixture.Compose(environmentName: "Development", preserveCountryEnvironment: true);
        using var application = builder.Build();
        var country = await EnvironmentFor(builder, "legacy-maliev-country-service");
        Assert.DoesNotContain(country.Keys, key => key.StartsWith("ServiceAuthentication__", StringComparison.Ordinal));
    }

    [Fact]
    public void CountryWorkloadEnvironmentRestorationKeepsCommandLinePrecedence()
    {
        using var fixture = new GraphFixture();
        var jsonPath = Path.Combine(fixture.Root, "precedence.json");
        File.WriteAllText(jsonPath, "{\"CountryWorkload\":{\"Enabled\":false}}");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(jsonPath, optional: false, reloadOnChange: false);
        configuration.AddCommandLine(["--CountryWorkload:Enabled=false"]);
        CountryWorkloadConfiguration.RestoreEnvironment(configuration,
            new Dictionary<string, string?> { ["CountryWorkload:Enabled"] = "true" });
        Assert.Equal("false", configuration["CountryWorkload:Enabled"]);
    }

    [Fact]
    public void CountryWorkloadEnvironmentRestorationOverridesFileConfiguration()
    {
        using var fixture = new GraphFixture();
        var jsonPath = Path.Combine(fixture.Root, "precedence.json");
        File.WriteAllText(jsonPath, "{\"CountryWorkload\":{\"Enabled\":false}}");
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(jsonPath, optional: false, reloadOnChange: false);
        CountryWorkloadConfiguration.RestoreEnvironment(configuration,
            new Dictionary<string, string?> { ["CountryWorkload:Enabled"] = "true" });
        Assert.Equal("true", configuration["CountryWorkload:Enabled"]);
    }

    private static Dictionary<string, string?> CountryWorkloadFixtureConfiguration()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        return new()
        {
            ["CountryWorkload:Enabled"] = "true",
            ["CountryWorkload:ClientId"] = "owned-country",
            ["CountryWorkload:ClientSecret"] = secret,
            ["CountryWorkload:SecretSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            ["CountryWorkload:IamOrigin"] = "https://owned-iam.invalid",
            ["CountryWorkload:Permissions:0"] = "legacy-country.countries.read",
            ["CountryWorkload:Permissions:1"] = "legacy-country.countries.update"
        };
    }

    [Theory]
    [InlineData(15)]
    [InlineData(1025)]
    public void CountryWorkloadRejectsSecretOutsideSharedAuthBounds(int length)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        var secret = new string('x', length);
        configuration["CountryWorkload:ClientSecret"] = secret;
        configuration["CountryWorkload:SecretSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        Assert.Throws<InvalidOperationException>(() => fixture.Compose(configuration, "Development"));
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Fact]
    public async Task CountryWorkloadCanProjectOnlyTheFourExplicitProducerPermissions()
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        string[] permissions = ["legacy-country.countries.read", "legacy-country.countries.create", "legacy-country.countries.update", "legacy-country.countries.delete"];
        for (var index = 0; index < permissions.Length; index++) configuration[$"CountryWorkload:Permissions:{index}"] = permissions[index];
        var builder = fixture.Compose(configuration, "Development");
        using var application = builder.Build();
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");
        for (var index = 0; index < permissions.Length; index++) Assert.Equal(permissions[index], auth[$"ServiceClients__Clients__owned-country__Permissions__{index}"]);
        Assert.DoesNotContain("ServiceClients__Clients__owned-country__Permissions__4", auth.Keys);
    }

    [Fact]
    public void CountryWorkloadRejectsNonContiguousGrantIndexes()
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        configuration.Remove("CountryWorkload:Permissions:1");
        configuration["CountryWorkload:Permissions:2"] = "legacy-country.countries.update";
        Assert.Throws<InvalidOperationException>(() => fixture.Compose(configuration, "Development"));
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Theory]
    [InlineData("http://127.0.0.1:14001")]
    [InlineData("https://owned-iam.invalid")]
    public void CountryWorkloadRejectsProductionOptInBeforeCreatingSecrets(string origin)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        configuration["CountryWorkload:IamOrigin"] = origin;
        Assert.Throws<InvalidOperationException>(() => fixture.Compose(configuration, "Production"));
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Theory]
    [InlineData("owned-country")]
    [InlineData("OWNED-COUNTRY")]
    public void CountryWorkloadCannotOverwriteAnExistingConfiguredAuthCaller(string registeredClientId)
    {
        using var fixture = new GraphFixture();
        var configuration = CountryWorkloadFixtureConfiguration();
        var hashKey = $"ServiceClients:Clients:{registeredClientId}:SecretSha256";
        var permissionKey = $"ServiceClients:Clients:{registeredClientId}:Permissions:0";
        var existingHash = Convert.ToHexStringLower(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));
        configuration[hashKey] = existingHash;
        configuration[permissionKey] = "existing.custom.permission";
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Compose(configuration, "Development"));
        Assert.DoesNotContain(configuration["CountryWorkload:ClientSecret"]!, error.Message, StringComparison.Ordinal);
        Assert.Equal(existingHash, configuration[hashKey]);
        Assert.Equal("existing.custom.permission", configuration[permissionKey]);
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    [Fact]
    public async Task AuthProjectsGeneratedClientHashAndPermissionsForRealWebAndBffSecrets()
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        var auth = await EnvironmentFor(builder, "legacy-maliev-auth-service");
        foreach ((string resourceName, string clientId) in new[]
        {
            ("legacy-maliev-web", "legacy-web"), ("legacy-maliev-intranet-bff", "legacy-intranet")
        })
        {
            var client = await EnvironmentFor(builder, resourceName);
            Assert.Equal(clientId, client["ServiceAuthentication__ClientId"]);
            string secret = Assert.IsType<string>(client["ServiceAuthentication__ClientSecret"]);
            string expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
            Assert.Equal(expectedHash, auth[$"ServiceClients__Clients__{clientId}__SecretSha256"]);
            IEnumerable<string> expectedPermissions = clientId == "legacy-intranet"
                ? LegacyTopology.IntranetPermissions
                : new[]
            {
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
                "legacy-catalog.countries.read",
                "legacy-catalog.currencies.read",
                "legacy-catalog.materials.read",
                "legacy-catalog.material-groups.read",
                "legacy.orders.create",
                "legacy.order-catalog.read",
                "legacy.order-files.write",
                "legacy.order-status.write",
                "legacy.orders.delete"
            };
            string prefix = $"ServiceClients__Clients__{clientId}__Permissions__";
            Assert.Equal(expectedPermissions.Concat(new[] { "legacy-catalog.locations.read", "legacy-catalog.companies.read" })
                .Order(StringComparer.Ordinal), auth
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(entry => Assert.IsType<string>(entry.Value)).Order(StringComparer.Ordinal));
            Assert.DoesNotContain("Jwt__PrivateKeyPem", client.Keys);
        }
    }

    [Theory]
    [InlineData("Quotation", "legacy-maliev-quotation-service")]
    [InlineData("Catalog", "legacy-maliev-catalog-service")]
    public async Task AccountingInvoiceClientsReceiveActualEndpointsDiscoveryAndHealthyStartupDependencies(string service, string resourceName)
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        var accounting = Assert.Single(builder.Resources, resource => resource.Name == "legacy-maliev-accounting-service");
        var downstream = Assert.Single(builder.Resources, resource => resource.Name == resourceName);
        var values = await EnvironmentFor(builder, accounting.Name);
        foreach (string key in new[] { $"Services__{service}", $"services__{resourceName}__http__0" })
        {
            var endpoint = Assert.IsType<EndpointReference>(values[key]);
            Assert.Same(downstream, endpoint.Resource);
            Assert.Equal("http", endpoint.EndpointName);
        }
        Assert.Contains(accounting.Annotations.OfType<EndpointReferenceAnnotation>(), reference => ReferenceEquals(reference.Resource, downstream));
        Assert.Contains(accounting.Annotations.OfType<WaitAnnotation>(), wait =>
            ReferenceEquals(wait.Resource, downstream) && wait.WaitType == WaitType.WaitUntilHealthy);
        Assert.Equal("legacy-accounting", values["ServiceAuthentication__ClientId"]);
        Assert.False(ReachesAccounting(downstream, []));

        bool ReachesAccounting(IResource resource, HashSet<IResource> visited)
        {
            if (ReferenceEquals(resource, accounting)) return true;
            return visited.Add(resource) && resource.Annotations.OfType<WaitAnnotation>()
                .Any(wait => ReachesAccounting(wait.Resource, visited));
        }
    }

    [Fact]
    public void WebAndBffHaveTheirDeclaredBrowserEndpointsAndActualAuthRedisWaits()
    {
        using var fixture = new GraphFixture();
        var builder = fixture.Compose();
        using var application = builder.Build();
        foreach (string name in new[] { "legacy-maliev-web", "legacy-maliev-intranet-bff" })
        {
            IResource resource = Assert.Single(builder.Resources, resource => resource.Name == name);
            string endpointName = name == "legacy-maliev-web" ? "http" : "https";
            Assert.Contains(resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == endpointName);
            Assert.Contains(resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource.Name == "legacy-redis");
            Assert.Contains(resource.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource.Name == "legacy-maliev-auth-service");
        }
        var bff = Assert.Single(builder.Resources, resource => resource.Name == "legacy-maliev-intranet-bff");
        foreach (string downstream in new[] { "customer", "procurement", "document", "file", "notification", "accounting" })
            Assert.DoesNotContain(bff.Annotations.OfType<WaitAnnotation>(), wait => wait.Resource.Name == $"legacy-maliev-{downstream}-service");
        var country = Assert.Single(builder.Resources, resource => resource.Name == "legacy-maliev-country-service");
        Assert.Contains(country.Annotations.OfType<WaitAnnotation>(), wait =>
            wait.Resource.Name == "legacy-country-migrations" && wait.WaitType == WaitType.WaitForCompletion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentReviewOrDeltaOnlyDeclaresOwnedVolumeAndPreservesAuthRuntimeMigration(bool apply)
    {
        using var fixture = new GraphFixture();
        fixture.Set(apply ? "LEGACY_LOCAL_DELTA" : "LEGACY_LOCAL_DELTA_REVIEW", "true");
        fixture.Set("LEGACY_LOCAL_DELTA_CONFIG", Path.Combine(fixture.Root, "inert-review-config.json"));
        var builder = fixture.Compose();
        using var application = builder.Build();
        var postgres = Assert.Single(builder.Resources.OfType<PostgresServerResource>());
        Assert.Contains(postgres.Annotations.OfType<ContainerMountAnnotation>(), mount =>
            mount.Source == PersistentLocalDeltaReviewContract.PostgresVolumeName &&
            mount.Target == PersistentLocalDeltaReviewContract.PostgresVolumeTarget);
        Assert.Equal("true", (await EnvironmentFor(builder, "legacy-country-migrations"))["LEGACY_SKIP_MIGRATE"]);
        Assert.Equal("false", (await EnvironmentFor(builder, "legacy-auth-migrations"))["LEGACY_SKIP_MIGRATE"]);
        Assert.Equal(apply ? 1 : 0, builder.Resources.Count(resource => resource.Name == "legacy-local-delta-apply"));
        var countryMigration = Assert.Single(builder.Resources, resource => resource.Name == "legacy-country-migrations");
        var deltaWaits = countryMigration.Annotations.OfType<WaitAnnotation>()
            .Where(wait => wait.Resource.Name == "legacy-local-delta-apply").ToArray();
        Assert.Equal(apply ? 1 : 0, deltaWaits.Length);
        if (apply) Assert.Equal(WaitType.WaitForCompletion, deltaWaits[0].WaitType);
        // Mount annotation only: no Docker call, volume adoption, migration or persistent access.
    }

    [Fact]
    public async Task SnapshotIdentityIsCapturedBeforeSanitizationAndProjectedExplicitly()
    {
        using var fixture = new GraphFixture();
        string keyPath = Path.Combine(fixture.Root, "fixture-key.txt");
        File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        LocalDeltaExecution.ProtectFile(keyPath);
        fixture.Set("LEGACY_LOCAL_SNAPSHOT", "true");
        fixture.Set("LEGACY_LOCAL_SNAPSHOT_DIR", fixture.Root);
        fixture.Set("LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE", keyPath);
        fixture.Set("LEGACY_LOCAL_SNAPSHOT_ID", "owned-composition-snapshot");
        var builder = fixture.Compose();
        using var application = builder.Build();
        var runner = await EnvironmentFor(builder, "legacy-country-migrations");
        Assert.Equal(fixture.Root, runner["LEGACY_SNAPSHOT_DIRECTORY"]);
        Assert.Equal(keyPath, runner["LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE"]);
        Assert.Equal("owned-composition-snapshot", runner["LEGACY_SNAPSHOT_ID"]);
        Assert.Equal("true", runner["LEGACY_SKIP_MIGRATE"]);
        string[] standardConsumers =
        [
            "legacy-country-migrations", "legacy-auth-migrations", "legacy-customer-identity-migrations",
            "legacy-employee-identity-migrations", "legacy-customer-migrations", "legacy-employee-migrations",
            "legacy-catalog-migrations", "legacy-supplier-migrations", "legacy-purchase-order-migrations",
            "legacy-file-migrations", "legacy-order-migrations", "legacy-order-status-migrations",
            "legacy-quotation-migrations", "legacy-quotation-request-migrations", "legacy-career-migrations",
            "legacy-contact-migrations", "legacy-payment-migrations", "legacy-invoice-migrations", "legacy-receipt-migrations"
        ];
        string[] preservedConsumers =
        [
            "legacy-contact-request-snapshot", "legacy-currency-snapshot", "legacy-data-protection-keys-snapshot",
            "legacy-data-protection-keys-employee-snapshot", "legacy-location-data-snapshot"
        ];
        Assert.Equal(preservedConsumers.Order(StringComparer.Ordinal), builder.Resources.OfType<ProjectResource>()
            .Where(resource => resource.Name.EndsWith("-snapshot", StringComparison.Ordinal))
            .Select(resource => resource.Name).Order(StringComparer.Ordinal));
        foreach (string consumer in standardConsumers.Concat(preservedConsumers))
        {
            var environment = await EnvironmentFor(builder, consumer);
            Assert.Equal(fixture.Root, environment["LEGACY_SNAPSHOT_DIRECTORY"]);
            Assert.Equal(keyPath, environment["LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE"]);
            Assert.Equal("owned-composition-snapshot", environment["LEGACY_SNAPSHOT_ID"]);
            Assert.Equal(consumer == "legacy-auth-migrations" ? "false" : "true", environment["LEGACY_SKIP_MIGRATE"]);
            if (preservedConsumers.Contains(consumer, StringComparer.Ordinal))
            {
                Assert.IsType<ReferenceExpression>(environment["ConnectionStrings__SnapshotDb"]);
                Assert.Equal("false", environment["LEGACY_LOCAL_ALLOW_NONEMPTY_MIGRATE"]);
            }
        }
        Assert.Null(Environment.GetEnvironmentVariable("LEGACY_LOCAL_SNAPSHOT_ID"));
        // No archive restore: skip plus snapshot is not a harmless entrypoint execution.
    }

    [Fact]
    public void InvalidModesRefuseBeforeSanitizationAndCertificateMaterialization()
    {
        using var fixture = new GraphFixture();
        fixture.Set("LEGACY_LOCAL_SNAPSHOT", "true");
        fixture.Set("LEGACY_LOCAL_DELTA_REVIEW", "true");
        fixture.Set("APPHOST_TEST_CAPTURE_SENTINEL", "must-survive-refusal");
        Assert.Throws<InvalidOperationException>(() => fixture.Compose());
        Assert.Equal("must-survive-refusal", Environment.GetEnvironmentVariable("APPHOST_TEST_CAPTURE_SENTINEL"));
        Assert.False(Directory.Exists(fixture.CertificateDirectory));
    }

    private static async Task<Dictionary<string, object>> EnvironmentFor(IDistributedApplicationBuilder builder, string name)
    {
        IResource resource = Assert.Single(builder.Resources, resource => resource.Name == name);
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, CancellationToken.None);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }
        return values;
    }

    private sealed class GraphFixture : IDisposable
    {
        private static readonly IReadOnlyDictionary<string, string?> SyntheticConfiguration = new Dictionary<string, string?>
        {
            ["Parameters:legacy-postgres-username"] = "owned-fixture",
            ["Parameters:legacy-postgres-password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Parameters:legacy-redis-password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            ["Parameters:legacy-web-google-maps-embed-api-key"] = "owned-fixture-maps-embed",
            ["Parameters:legacy-intranet-google-maps-browser-api-key"] = "owned-fixture-maps-browser",
            ["Authentication:Google:ClientId"] = "owned-fixture-google-client",
            ["GoogleIdentity:Employee:HostedDomain"] = "owned-fixture.invalid",
            ["GoogleIdentity:Employee:Audiences:intranet"] = "owned-fixture-google-client"
        };
        private readonly Dictionary<string, string?> original = Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "maliev-build-graph-" + Guid.NewGuid().ToString("N"));
        public string CertificateDirectory => Path.Combine(Root, "certificates");
        public string PostgresPassword => SyntheticConfiguration["Parameters:legacy-postgres-password"]!;

        public GraphFixture()
        {
            try
            {
                Directory.CreateDirectory(Root);
                foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray())
                {
                    if (new[] { "ASPIRE_", "ASPNETCORE_", "DOTNET_", "Parameters__", "MALIEV_", "LEGACY_", "Authentication__", "GoogleIdentity__", "ConnectionStrings__", "CountryWorkload__", "CountryWorkload:" }
                        .Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) Set(name, null);
                }
                Set("DOTNET_ENVIRONMENT", "Production");
                Set("ASPNETCORE_ENVIRONMENT", "Production");
                Set("APPDATA", Path.Combine(Root, "appdata"));
                Set("LOCALAPPDATA", Path.Combine(Root, "localappdata"));
                Set("USERPROFILE", Path.Combine(Root, "profile"));
                foreach (string name in new[] { "LEGACY_GKE_VALIDATION", "LEGACY_LOCAL_SNAPSHOT", "LEGACY_LOCAL_DELTA",
                    "LEGACY_LOCAL_DELTA_REVIEW", "LEGACY_LOCAL_FIXTURES", "LEGACY_LOCAL_SNAPSHOT_DIR",
                    "LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE", "LEGACY_LOCAL_SNAPSHOT_ID", "LEGACY_LOCAL_DELTA_CONFIG" })
                {
                    Set(name, null);
                }
                Set("LEGACY_WEB_PROJECT", new Projects.Legacy_Maliev_Web().ProjectPath);
                Set("LEGACY_WEB_REPOSITORY", "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web");
                Set("LEGACY_WEB_BRANCH", "owned-build-only-fixture");
                Set("LEGACY_WEB_COMMIT", "007c082b4abd3da36d335f55a98702f507661651");
                Set("LEGACY_WEB_PORT", "59154");
                Set("Parameters__legacy-postgres-username", "owned-fixture");
                Set("Parameters__legacy-postgres-password", PostgresPassword);
                Set("Parameters__legacy-redis-password", SyntheticConfiguration["Parameters:legacy-redis-password"]);
                Set("Parameters__legacy-web-google-maps-embed-api-key", "owned-fixture-maps-embed");
                Set("Parameters__legacy-intranet-google-maps-browser-api-key", "owned-fixture-maps-browser");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

        public IDistributedApplicationBuilder Compose(IReadOnlyDictionary<string, string?>? additionalConfiguration = null, string? environmentName = null, bool preserveCountryEnvironment = false)
        {
            if (environmentName is not null)
            {
                Set("DOTNET_ENVIRONMENT", environmentName);
                Set("ASPNETCORE_ENVIRONMENT", environmentName);
            }
            return AppHostComposition.CreateBuilder([], CertificateDirectory,
                new DistributedApplicationOptions
                {
                    AssemblyName = typeof(AppHostComposition).Assembly.GetName().Name!,
                    ProjectDirectory = Root,
                    DisableDashboard = true,
                    TrustDeveloperCertificate = false
                }, builder =>
                {
                    // Observe the actual restored builder configuration before replacing unrelated operator sources.
                    var countryEnvironment = preserveCountryEnvironment
                        ? builder.Configuration.AsEnumerable().Where(entry => entry.Key.StartsWith("CountryWorkload:", StringComparison.OrdinalIgnoreCase)).ToArray()
                        : [];
                    // Owned empty directories and provider replacement prevent loading operator configuration.
                    builder.Configuration.Sources.Clear();
                    builder.Configuration.AddInMemoryCollection(SyntheticConfiguration);
                    if (countryEnvironment.Length > 0) builder.Configuration.AddInMemoryCollection(countryEnvironment);
                    if (additionalConfiguration is not null) builder.Configuration.AddInMemoryCollection(additionalConfiguration);
                });
        }

        public void Dispose()
        {
            foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToArray())
            {
                if (!original.ContainsKey(name)) Set(name, null);
            }
            foreach (var entry in original) Set(entry.Key, entry.Value);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
