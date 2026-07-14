using Legacy.Maliev.CountryService.Data;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__CountryDbContext");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("The Country database connection string is required.");
}

var options = new DbContextOptionsBuilder<CountryDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var dbContext = new CountryDbContext(options);
await dbContext.Database.MigrateAsync();
