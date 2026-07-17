using Legacy.Maliev.AppHost.Topology;

LocalEnvironmentPolicy.SanitizeCurrentProcess();

var builder = DistributedApplication.CreateBuilder(args);

var postgresUsername = builder.AddParameter("legacy-postgres-username");
var postgresPassword = builder.AddParameter("legacy-postgres-password", secret: true);
var redisPassword = builder.AddParameter("legacy-redis-password", secret: true);
var jwt = LocalJwtKeyMaterial.Create();
var webCredential = LocalServiceCredential.Create();
var intranetCredential = LocalServiceCredential.Create();
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
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__4", "legacy-customer.customers.read")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__5", "legacy-customer.customers.update")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__6", "legacy-customer.addresses.create")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__7", "legacy-customer.addresses.update")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__8", "legacy-customer.companies.create")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__9", "legacy-customer.companies.update")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__10", "legacy-customer.companies.delete")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__11", "legacy.customer-orders.read")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__12", "legacy.customer-orders.cancel")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__13", "legacy.customer-quotations.read")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__14", "legacy-contact.messages.create")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__15", "legacy.quotation-requests.create")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__16", "legacy.quotation-files.write")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__17", "legacy-file.uploads.create")
    .WithEnvironment("ServiceClients__Clients__legacy-web__Permissions__18", "legacy-file.uploads.delete")
    .WithEnvironment("ServiceClients__Clients__legacy-intranet__SecretSha256", intranetCredential.SecretSha256)
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

for (var permissionIndex = 0; permissionIndex < LegacyTopology.IntranetPermissions.Count; permissionIndex++)
{
    auth.WithEnvironment(
        $"ServiceClients__Clients__legacy-intranet__Permissions__{permissionIndex}",
        LegacyTopology.IntranetPermissions[permissionIndex]);
}

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

var employeeDatabase = databases["Employee"];
var employeeMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-employee-migrations")
    .WithArgs("employee")
    .WithEnvironment("ConnectionStrings__EmployeeDbContext", employeeDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(employeeDatabase);

var employee = builder.AddProject<Projects.Legacy_Maliev_EmployeeService_Api>(
        "legacy-maliev-employee-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__EmployeeDbContext", employeeDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("AuthService__LegacyEmployeeIdentityBaseUrl", ReferenceExpression.Create($"{auth.GetEndpoint("http")}/auth/v1/legacy/employees/"))
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/employee/liveness", endpointName: "http")
    .WithHttpHealthCheck("/employee/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/employee/scalar";
        url.DisplayText = "Employee Scalar";
    })
    .WaitForCompletion(employeeMigrations)
    .WaitFor(redis)
    .WaitFor(auth);

var catalogDatabase = databases["Material"];
var catalogMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-catalog-migrations")
    .WithArgs("catalog")
    .WithEnvironment("ConnectionStrings__CatalogDbContext", catalogDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(catalogDatabase);

var catalog = builder.AddProject<Projects.Legacy_Maliev_CatalogService_Api>(
        "legacy-maliev-catalog-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__CatalogDbContext", catalogDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/catalog/liveness", endpointName: "http")
    .WithHttpHealthCheck("/catalog/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/catalog/scalar";
        url.DisplayText = "Catalog Scalar";
    })
    .WaitForCompletion(catalogMigrations)
    .WaitFor(redis)
    .WaitFor(auth);

var supplierDatabase = databases["Supplier"];
var purchaseOrderDatabase = databases["PurchaseOrder"];
var supplierMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-supplier-migrations")
    .WithArgs("supplier")
    .WithEnvironment("ConnectionStrings__SupplierDbContext", supplierDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(supplierDatabase);
var purchaseOrderMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-purchase-order-migrations")
    .WithArgs("purchase-order")
    .WithEnvironment("ConnectionStrings__PurchaseOrderDbContext", purchaseOrderDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(purchaseOrderDatabase);

var procurement = builder.AddProject<Projects.Legacy_Maliev_ProcurementService_Api>(
        "legacy-maliev-procurement-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__SupplierDbContext", supplierDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__PurchaseOrderDbContext", purchaseOrderDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/procurement/liveness", endpointName: "http")
    .WithHttpHealthCheck("/procurement/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/procurement/scalar";
        url.DisplayText = "Procurement Scalar";
    })
    .WaitForCompletion(supplierMigrations)
    .WaitForCompletion(purchaseOrderMigrations)
    .WaitFor(redis)
    .WaitFor(auth);

var fileDatabase = databases["Upload"];
var fileMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-file-migrations")
    .WithArgs("file")
    .WithEnvironment("ConnectionStrings__FileDbContext", fileDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(fileDatabase);

var file = builder.AddProject<Projects.Legacy_Maliev_FileService_Api>(
        "legacy-maliev-file-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__FileDbContext", fileDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/file/liveness", endpointName: "http")
    .WithHttpHealthCheck("/file/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/file/scalar";
        url.DisplayText = "File Scalar";
    })
    .WaitForCompletion(fileMigrations)
    .WaitFor(auth);

var notification = builder.AddProject<Projects.Legacy_Maliev_NotificationService_Api>(
        "legacy-maliev-notification-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Notifications__UseDevelopmentRecordingProvider", "true")
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

var orderDatabase = databases["Order"];
var orderStatusDatabase = databases["OrderStatus"];
var orderMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-order-migrations")
    .WithArgs("order")
    .WithEnvironment("ConnectionStrings__OrderDbContext", orderDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(orderDatabase);
var orderStatusMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>(
        "legacy-order-status-migrations")
    .WithArgs("order-status")
    .WithEnvironment("ConnectionStrings__OrderStatusDbContext", orderStatusDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(orderStatusDatabase);

var order = builder.AddProject<Projects.Legacy_Maliev_OrderService_Api>(
        "legacy-maliev-order-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__OrderDbContext", orderDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__OrderStatusDbContext", orderStatusDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/order/liveness", endpointName: "http")
    .WithHttpHealthCheck("/order/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/order/scalar";
        url.DisplayText = "Order Scalar";
    })
    .WaitForCompletion(orderMigrations)
    .WaitForCompletion(orderStatusMigrations)
    .WaitFor(redis)
    .WaitFor(auth);

var quotationDatabase = databases["Quotation"];
var quotationRequestDatabase = databases["QuotationRequest"];
var quotationMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>(
        "legacy-quotation-migrations")
    .WithArgs("quotation")
    .WithEnvironment("ConnectionStrings__QuotationDbContext", quotationDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(quotationDatabase);
var quotationRequestMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>(
        "legacy-quotation-request-migrations")
    .WithArgs("quotation-request")
    .WithEnvironment("ConnectionStrings__QuotationRequestDbContext", quotationRequestDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(quotationRequestDatabase);

var quotation = builder.AddProject<Projects.Legacy_Maliev_QuotationService_Api>(
        "legacy-maliev-quotation-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__QuotationDbContext", quotationDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__QuotationRequestDbContext", quotationRequestDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/quotation/liveness", endpointName: "http")
    .WithHttpHealthCheck("/quotation/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/quotation/scalar";
        url.DisplayText = "Quotation Scalar";
    })
    .WaitForCompletion(quotationMigrations)
    .WaitForCompletion(quotationRequestMigrations)
    .WaitFor(redis)
    .WaitFor(auth);

var careerDatabase = databases["JobOffers"];
var careerMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-career-migrations")
    .WithArgs("career")
    .WithEnvironment("ConnectionStrings__CareerDbContext", careerDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(careerDatabase);
var career = builder.AddProject<Projects.Legacy_Maliev_CareerService_Api>(
        "legacy-maliev-career-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__CareerDbContext", careerDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/Jobs/liveness", endpointName: "http")
    .WithHttpHealthCheck("/Jobs/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/Jobs/scalar";
        url.DisplayText = "Career Scalar";
    })
    .WaitForCompletion(careerMigrations)
    .WaitFor(redis);

var contactDatabase = databases["Message"];
var contactMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-contact-migrations")
    .WithArgs("contact")
    .WithEnvironment("ConnectionStrings__ContactRequestDbContext", contactDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(contactDatabase);
var contact = builder.AddProject<Projects.Legacy_Maliev_ContactService_Api>(
        "legacy-maliev-contact-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__ContactRequestDbContext", contactDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/messages/liveness", endpointName: "http")
    .WithHttpHealthCheck("/messages/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/messages/scalar";
        url.DisplayText = "Contact Scalar";
    })
    .WaitForCompletion(contactMigrations)
    .WaitFor(redis);

var paymentDatabase = databases["Payment"];
var invoiceDatabase = databases["Invoice"];
var receiptDatabase = databases["Receipt"];
var paymentMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-payment-migrations")
    .WithArgs("payment")
    .WithEnvironment("ConnectionStrings__PaymentDbContext", paymentDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(paymentDatabase);
var invoiceMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-invoice-migrations")
    .WithArgs("invoice")
    .WithEnvironment("ConnectionStrings__InvoiceDbContext", invoiceDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(invoiceDatabase);
var receiptMigrations = builder.AddProject<Projects.Legacy_Maliev_AppHost_MigrationRunner>("legacy-receipt-migrations")
    .WithArgs("receipt")
    .WithEnvironment("ConnectionStrings__ReceiptDbContext", receiptDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WaitFor(receiptDatabase);
var accounting = builder.AddProject<Projects.Legacy_Maliev_AccountingService_Api>(
        "legacy-maliev-accounting-service",
        launchProfileName: "http")
    .ConfigureDynamicHttpEndpoint()
    .WithEnvironment("ConnectionStrings__PaymentDbContext", paymentDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__InvoiceDbContext", invoiceDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__ReceiptDbContext", receiptDatabase.Resource.ConnectionStringExpression)
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("DOTNET_GCHeapHardLimit", "134217728")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithEnvironment("NPGSQL_GSSAPI_AUTHENTICATION", "false")
    .WithEnvironment("PGGSSENCMODE", "disable")
    .WithHttpHealthCheck("/accounting/liveness", endpointName: "http")
    .WithHttpHealthCheck("/accounting/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/accounting/scalar";
        url.DisplayText = "Accounting Scalar";
    })
    .WaitForCompletion(paymentMigrations)
    .WaitForCompletion(invoiceMigrations)
    .WaitForCompletion(receiptMigrations)
    .WaitFor(redis);

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
    .WithEnvironment("Services__Order", order.GetEndpoint("http"))
    .WithEnvironment("Services__Quotation", quotation.GetEndpoint("http"))
    .WithEnvironment("Services__Career", career.GetEndpoint("http"))
    .WithEnvironment("Services__Contact", contact.GetEndpoint("http"))
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
    .WaitFor(order)
    .WaitFor(quotation)
    .WaitFor(notification)
    .WaitFor(career)
    .WaitFor(contact);

var intranetCompatibility = builder.AddProject<Projects.Legacy_Maliev_Intranet>("legacy-maliev-intranet")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("DataProtection__CertificatePfxBase64", dataProtectionCertificate.PfxBase64)
    .WithEnvironment("DataProtection__CertificatePassword", dataProtectionCertificate.Password)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("Jwt__KeyId", LegacyTopology.JwtKeyId)
    .WithEnvironment("ServiceAuthentication__ClientId", "legacy-intranet")
    .WithEnvironment("ServiceAuthentication__ClientSecret", intranetCredential.Secret)
    .WithEnvironment("Services__Auth", auth.GetEndpoint("http"))
    .WithEnvironment("Services__Catalog", catalog.GetEndpoint("http"))
    .WithEnvironment("Services__Customer", customer.GetEndpoint("http"))
    .WithEnvironment("Services__Employee", employee.GetEndpoint("http"))
    .WithEnvironment("Services__Procurement", procurement.GetEndpoint("http"))
    .WithEnvironment("Services__Document", document.GetEndpoint("http"))
    .WithEnvironment("Services__File", file.GetEndpoint("http"))
    .WithEnvironment("Services__Order", order.GetEndpoint("http"))
    .WithEnvironment("Services__Notification", notification.GetEndpoint("http"))
    .WithEnvironment("DOTNET_GCHeapHardLimit", "201326592")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithHttpHealthCheck("/intranet/liveness", endpointName: "http")
    .WithHttpHealthCheck("/intranet/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/Login";
        url.DisplayText = "Legacy Intranet";
    })
    .WithReference(redis)
    .WithReference(auth)
    .WaitFor(redis)
    .WaitFor(auth)
    .WaitFor(catalog)
    .WaitFor(customer)
    .WaitFor(employee)
    .WaitFor(procurement)
    .WaitFor(document)
    .WaitFor(file)
    .WaitFor(order)
    .WaitFor(notification);

var intranetBff = builder.AddProject<Projects.Legacy_Maliev_Intranet_Bff>("legacy-maliev-intranet-bff")
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("ConnectionStrings__redis", redis.Resource.ConnectionStringExpression)
    .WithEnvironment("DataProtection__CertificatePfxBase64", dataProtectionCertificate.PfxBase64)
    .WithEnvironment("DataProtection__CertificatePassword", dataProtectionCertificate.Password)
    .WithEnvironment("Jwt__PublicKey", jwt.PublicKeyBase64)
    .WithEnvironment("Jwt__Issuer", LegacyTopology.JwtIssuer)
    .WithEnvironment("Jwt__Audience", LegacyTopology.JwtAudience)
    .WithEnvironment("Jwt__KeyId", LegacyTopology.JwtKeyId)
    .WithEnvironment("ServiceAuthentication__ClientId", "legacy-intranet")
    .WithEnvironment("ServiceAuthentication__ClientSecret", intranetCredential.Secret)
    .WithEnvironment("Services__Auth", auth.GetEndpoint("http"))
    .WithEnvironment("Services__Catalog", catalog.GetEndpoint("http"))
    .WithEnvironment("DOTNET_GCHeapHardLimit", "201326592")
    .WithEnvironment("DOTNET_GCConserveMemory", "3")
    .WithHttpHealthCheck("/intranet-bff/liveness", endpointName: "http")
    .WithHttpHealthCheck("/intranet-bff/readiness", endpointName: "http")
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/Login";
        url.DisplayText = "Legacy Intranet BFF";
    })
    .WithReference(redis)
    .WithReference(auth)
    .WithReference(catalog)
    .WaitFor(redis)
    .WaitFor(auth)
    .WaitFor(catalog);

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
