using Legacy.Maliev.AppHost.Topology;

LocalEnvironmentPolicy.SanitizeCurrentProcess();

var builder = DistributedApplication.CreateBuilder(args);

var postgresUsername = builder.AddParameter("legacy-postgres-username");
var postgresPassword = builder.AddParameter("legacy-postgres-password", secret: true);
var redisPassword = builder.AddParameter("legacy-redis-password", secret: true);
var jwt = LocalJwtKeyMaterial.Create();
var webCredential = LocalServiceCredential.Create();
var dataProtectionCertificate = LocalDataProtectionCertificate.Create();

var postgres = builder.AddPostgres("legacy-postgres-main", postgresUsername, postgresPassword)
    .WithImageTag("18-alpine")
    .WithArgs(
        "-c", "max_connections=100",
        "-c", "shared_buffers=256MB",
        "-c", "effective_cache_size=768MB",
        "-c", "work_mem=2MB",
        "-c", "maintenance_work_mem=64MB",
        "-c", "wal_compression=on")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithContainerRuntimeArgs("--cpus", "0.75", "--memory", "1024m");

var databases = new Dictionary<string, IResourceBuilder<PostgresDatabaseResource>>(StringComparer.Ordinal);
foreach (var databaseName in LegacyTopology.DatabaseNames)
{
    var resourceName = $"legacy-{ToKebabCase(databaseName)}-db";
    databases.Add(databaseName, postgres.AddDatabase(resourceName, databaseName));
}

var authDatabase = postgres.AddDatabase("legacy-auth-db", "Auth");
var customerIdentityDatabase = databases["CustomerIdentity"];
var employeeIdentityDatabase = databases["EmployeeIdentity"];

var redis = builder.AddRedis("legacy-redis", port: null, password: redisPassword)
    .WithImageTag("8.4-alpine")
    .WithContainerRuntimeArgs("--cpus", "0.10", "--memory", "96m");

var countryDatabase = databases["Country"];
var countryMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-country-migrations")
    .WithArgs("country")
    .WithEnvironment("ConnectionStrings__CountryDbContext", countryDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(countryDatabase);

var country = builder.AddProject<Projects.Legacy_Maliev_CountryService_Api>("legacy-maliev-country-service")
    .WithEnvironment("ConnectionStrings__CountryDbContext", countryDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/countries/liveness", endpointName: "http")
    .WithHttpHealthCheck("/countries/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/countries/scalar";
        url.DisplayText = "Country Scalar";
    })
    .WaitForCompletion(countryMigrations)
    .WaitFor(redis);

var document = builder.AddProject<Projects.Legacy_Maliev_DocumentService_Api>("legacy-maliev-document-service")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "201326592")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithHttpHealthCheck("/documents/liveness", endpointName: "http")
    .WithHttpHealthCheck("/documents/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/documents/scalar";
        url.DisplayText = "Document Scalar";
    });

var authMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-auth-migrations")
    .WithArgs("auth")
    .WithEnvironment("ConnectionStrings__RefreshSessions", authDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(authDatabase);

var customerIdentityMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>(
        "legacy-customer-identity-migrations")
    .WithArgs("customer-identity")
    .WithEnvironment("ConnectionStrings__CustomerIdentity", customerIdentityDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(customerIdentityDatabase);

var employeeIdentityMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>(
        "legacy-employee-identity-migrations")
    .WithArgs("employee-identity")
    .WithEnvironment("ConnectionStrings__EmployeeIdentity", employeeIdentityDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(employeeIdentityDatabase);

var auth = builder.AddProject<Projects.Legacy_Maliev_AuthService_Api>("legacy-maliev-auth-service")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("IdentityStorage__Provider", "PostgreSql")
    .WithEnvironment("ConnectionStrings__CustomerIdentity", customerIdentityDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__EmployeeIdentity", employeeIdentityDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__RefreshSessions", authDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("Jwt__PrivateKeyPem", jwt.PrivateKeyPem)
    .WithEnvironment("Jwt__KeyId", LegacyTopology.JwtKeyId)
    .WithEnvironment("ServiceClients__Clients__legacy-web__SecretSha256", webCredential.SecretSha256)
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__0", "legacy-auth.customer-self-service")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__1", "legacy-customer.customers.create")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__2", "legacy-customer.customers.delete")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__3", "legacy.notifications.send")
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithHttpHealthCheck("/auth/liveness", endpointName: "http")
    .WithHttpHealthCheck("/auth/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/auth/scalar";
        url.DisplayText = "Auth Scalar";
    })
    .WaitForCompletion(authMigrations)
    .WaitForCompletion(customerIdentityMigrations)
    .WaitForCompletion(employeeIdentityMigrations);

var customerDatabase = databases["Customer"];
var customerMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-customer-migrations")
    .WithArgs("customer")
    .WithEnvironment("ConnectionStrings__CustomerDbContext", customerDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(customerDatabase);

var customer = builder.AddProject<Projects.Legacy_Maliev_CustomerService_Api>(
        "legacy-maliev-customer-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__CustomerDbContext", customerDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("AuthService__LegacyCustomerIdentityBaseUrl", ReferenceExpression.Create($"{auth.GetEndpoint("http")}/auth/v1/legacy/customers/"))
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/customer/liveness", endpointName: "http")
    .WithHttpHealthCheck("/customer/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/customer/scalar";
        url.DisplayText = "Customer Scalar";
    })
    .WaitForCompletion(customerMigrations)
    .WaitFor(redis)
    .WaitFor(auth);

var notification = builder.AddProject<Projects.Legacy_Maliev_NotificationService_Api>(
        "legacy-maliev-notification-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Brevo__ApiKey", "development-placeholder")
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "100663296")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithHttpHealthCheck("/emails/liveness", endpointName: "http")
    .WithHttpHealthCheck("/emails/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/emails/scalar";
        url.DisplayText = "Notification Scalar";
    });

builder.AddProject<Projects.Legacy_Maliev_Web>("legacy-maliev-web")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("DataProtection__CertificatePfxBase64", dataProtectionCertificate.PfxBase64)
    .WithEnvironment("DataProtection__CertificatePassword", dataProtectionCertificate.Password)
    .WithEnvironment("ServiceAuthentication__ClientId", "legacy-web")
    .WithEnvironment("ServiceAuthentication__ClientSecret", webCredential.Secret)
    .WithEnvironment("Services__Auth", auth.GetEndpoint("http"))
    .WithEnvironment("Services__Customer", customer.GetEndpoint("http"))
    .WithEnvironment("Services__Notification", notification.GetEndpoint("http"))
    .WithEnvironment("Services__Country", country.GetEndpoint("http"))
    .WithEnvironment("Services__Document", document.GetEndpoint("http"))
    .WithEnvironment("DOTNET_GCHeapHardLimit", "201326592")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithHttpHealthCheck("/web/liveness", endpointName: "http")
    .WithHttpHealthCheck("/web/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/Account/Login";
        url.DisplayText = "Legacy Web";
    })
    .WaitFor(redis)
    .WaitFor(auth)
    .WaitFor(customer)
    .WaitFor(notification);

builder.Build().Run();

static string ToKebabCase(string value)
{
    var result = new System.Text.StringBuilder(value.Length + 8);
    for (var index = 0; index < value.Length; index++)
    {
        var character = value[index];
        if (index > 0 && char.IsUpper(character))
        {
            result.Append('-');
        }

        result.Append(char.ToLowerInvariant(character));
    }

    return result.ToString();
}

static class LocalEndpointExtensions
{
    public static IResourceBuilder<ProjectResource> ConfigureDynamicHttpEndpoint(
        this IResourceBuilder<ProjectResource> resource)
    {
        return resource.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = null;
            endpoint.TargetPort = null;
        });
    }
}
