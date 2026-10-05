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
            Assert.Equal(expectedPermissions.Order(StringComparer.Ordinal), auth
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(entry => Assert.IsType<string>(entry.Value)).Order(StringComparer.Ordinal));
            Assert.DoesNotContain("Jwt__PrivateKeyPem", client.Keys);
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
                    if (new[] { "ASPIRE_", "ASPNETCORE_", "DOTNET_", "Parameters__", "MALIEV_", "LEGACY_", "Authentication__", "GoogleIdentity__", "ConnectionStrings__" }
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
                Set("LEGACY_WEB_COMMIT", "e806c2c6bf3352ffe387436a40bf5c391b2f6377");
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

        public IDistributedApplicationBuilder Compose() => AppHostComposition.CreateBuilder([], CertificateDirectory,
            new DistributedApplicationOptions
            {
                AssemblyName = typeof(AppHostComposition).Assembly.GetName().Name!,
                ProjectDirectory = Root,
                DisableDashboard = true,
                TrustDeveloperCertificate = false
            }, builder =>
            {
                // Empty owned project directory and Production prevent default operator JSON/user-secret loading.
                // Replace providers before any graph parameter or identity is read.
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection(SyntheticConfiguration);
            });

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
