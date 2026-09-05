using Npgsql;
using Testcontainers.PostgreSql;
using Legacy.Maliev.AppHost.MigrationRunner;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class CatalogSnapshotComposerTests
{
    [Fact]
    public async Task CurrencySnapshotReplacesOnlyCatalogCurrencyProjectionAndIsRepeatable()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await postgres.StartAsync();

        var admin = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "postgres" };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE DATABASE \"Currency\"; CREATE DATABASE \"Material\";";
            await command.ExecuteNonQueryAsync();
        }

        var source = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "Currency" };
        var target = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "Material" };
        await ExecuteAsync(source.ConnectionString, """
            CREATE TABLE "Currency" (
                "ID" integer PRIMARY KEY,
                "ShortName" character varying(10) NOT NULL,
                "LongName" character varying(50) NOT NULL,
                "CreatedDate" timestamp without time zone NULL,
                "ModifiedDate" timestamp without time zone NULL);
            INSERT INTO "Currency" VALUES
                (138, 'THB', 'Baht', TIMESTAMP '2018-07-23 19:51:25', NULL),
                (152, 'USD', 'US Dollar', NULL, TIMESTAMP '2018-09-30 19:30:04');
            """);
        await ExecuteAsync(target.ConnectionString, """
            CREATE TABLE "Material" ("ID" integer PRIMARY KEY, "Name" text NOT NULL);
            INSERT INTO "Material" VALUES (1, 'Aluminium');
            CREATE TABLE "Currency" (
                "ID" integer PRIMARY KEY,
                "ShortName" character varying(10) NOT NULL,
                "LongName" character varying(50) NOT NULL,
                "CreatedDate" timestamp without time zone NULL,
                "ModifiedDate" timestamp without time zone NULL);
            INSERT INTO "Currency" VALUES (999, 'OLD', 'Stale row', NULL, NULL);
            """);

        await CatalogSnapshotComposer.ComposeCurrenciesAsync(source.ConnectionString, target.ConnectionString);
        await CatalogSnapshotComposer.ComposeCurrenciesAsync(source.ConnectionString, target.ConnectionString);

        Assert.Equal(1L, await ScalarAsync<long>(target.ConnectionString, "SELECT COUNT(*) FROM \"Material\""));
        Assert.Equal(2L, await ScalarAsync<long>(target.ConnectionString, "SELECT COUNT(*) FROM \"Currency\""));
        Assert.Equal("THB", await ScalarAsync<string>(target.ConnectionString, "SELECT \"ShortName\" FROM \"Currency\" WHERE \"ID\" = 138"));
        Assert.Equal(0L, await ScalarAsync<long>(target.ConnectionString, "SELECT COUNT(*) FROM \"Currency\" WHERE \"ID\" = 999"));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected scalar result."));
    }
}
