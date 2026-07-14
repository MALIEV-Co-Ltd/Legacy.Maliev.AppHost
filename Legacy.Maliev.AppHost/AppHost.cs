using Legacy.Maliev.AppHost.Topology;

LocalEnvironmentPolicy.SanitizeCurrentProcess();

var builder = DistributedApplication.CreateBuilder(args);

var postgresUsername = builder.AddParameter("legacy-postgres-username");
var postgresPassword = builder.AddParameter("legacy-postgres-password", secret: true);
var redisPassword = builder.AddParameter("legacy-redis-password", secret: true);
var jwt = LocalJwtKeyMaterial.Create();

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

var redis = builder.AddRedis("legacy-redis", port: null, password: redisPassword)
    .WithImageTag("8.4-alpine")
    .WithContainerRuntimeArgs("--cpus", "0.10", "--memory", "96m");

var countryDatabase = databases["Country"];
builder.AddProject<Projects.Legacy_Maliev_CountryService_Api>("legacy-maliev-country-service")
    .WithEnvironment("ConnectionStrings__CountryDbContext", countryDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", "https://legacy-iam.localhost")
    .WithEnvironment("Jwt__Audience", "maliev-legacy-services")
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
    .WaitFor(postgres)
    .WaitFor(redis);

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
