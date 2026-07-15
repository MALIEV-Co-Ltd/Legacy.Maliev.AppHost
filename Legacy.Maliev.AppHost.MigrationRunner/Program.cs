using Legacy.Maliev.AuthService.Infrastructure;
using Legacy.Maliev.AppHost.Topology;
using Legacy.Maliev.CountryService.Data;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var workload = args.SingleOrDefault()
    ?? throw new InvalidOperationException("A migration workload is required.");
var connectionName = workload switch
{
    "auth" => "RefreshSessions",
    "customer-identity" => "CustomerIdentity",
    "employee-identity" => "EmployeeIdentity",
    "country" => "CountryDbContext",
    "customer" => "CustomerDbContext",
    _ => throw new InvalidOperationException($"Unknown migration workload '{workload}'."),
};
var connectionString = Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionName}");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException($"The {connectionName} connection string is required.");
}

await MigrateAsync(workload, connectionString);

static async Task MigrateAsync(string workload, string connectionString)
{
    switch (workload)
    {
        case "auth":
            var authOptions = new DbContextOptionsBuilder<RefreshSessionDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new RefreshSessionDbContext(authOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "country":
            var countryOptions = new DbContextOptionsBuilder<CountryDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CountryDbContext(countryOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
        case "customer-identity":
            var customerIdentityOptions = new DbContextOptionsBuilder<CustomerIdentityDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CustomerIdentityDbContext(customerIdentityOptions))
            {
                await context.Database.MigrateAsync();
                await SeedIdentityAsync(
                    context,
                    "local-customer",
                    LegacyTopology.LocalCustomerEmail,
                    databaseId: 1,
                    includeCustomerFields: true);
            }

            break;
        case "employee-identity":
            var employeeIdentityOptions = new DbContextOptionsBuilder<EmployeeIdentityDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new EmployeeIdentityDbContext(employeeIdentityOptions))
            {
                await context.Database.MigrateAsync();
                await SeedIdentityAsync(
                    context,
                    "local-employee",
                    LegacyTopology.LocalEmployeeEmail,
                    databaseId: 2,
                    includeCustomerFields: false);
            }

            break;
        case "customer":
            var customerOptions = new DbContextOptionsBuilder<CustomerDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using (var context = new CustomerDbContext(customerOptions))
            {
                await context.Database.MigrateAsync();
            }

            break;
    }
}

static async Task SeedIdentityAsync(
    LegacyIdentityDbContext context,
    string id,
    string email,
    int databaseId,
    bool includeCustomerFields)
{
    var normalizedEmail = email.ToUpperInvariant();
    if (await context.Users.AsNoTracking().AnyAsync(row => row.NormalizedUserName == normalizedEmail))
    {
        return;
    }

    var row = new LegacyIdentityRow
    {
        Id = id,
        UserName = email,
        NormalizedUserName = normalizedEmail,
        Email = email,
        NormalizedEmail = normalizedEmail,
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        DatabaseID = databaseId,
        FaxNumber = includeCustomerFields ? "local-only" : null,
        MobileNumber = includeCustomerFields ? "local-only" : null,
        LockoutEnabled = true
    };
    row.PasswordHash = new PasswordHasher<LegacyIdentityRow>()
        .HashPassword(row, LegacyTopology.LocalIdentityPassword);
    context.Users.Add(row);
    await context.SaveChangesAsync();
}
