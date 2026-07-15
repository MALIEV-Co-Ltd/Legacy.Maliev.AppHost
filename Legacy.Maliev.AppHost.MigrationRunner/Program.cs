using Legacy.Maliev.AuthService.Infrastructure;
using Legacy.Maliev.CountryService.Data;
using Legacy.Maliev.CustomerService.Data;
using Microsoft.EntityFrameworkCore;

var workload = args.SingleOrDefault()
    ?? throw new InvalidOperationException("A migration workload is required.");
var connectionName = workload switch
{
    "auth" => "RefreshSessions",
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
