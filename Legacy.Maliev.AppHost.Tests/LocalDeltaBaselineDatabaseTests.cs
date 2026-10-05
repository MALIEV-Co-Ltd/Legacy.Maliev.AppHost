using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.AppHost.Tests;

[Collection("AppHostComposition")]
public sealed class LocalDeltaBaselineDatabaseTests(LocalDeltaBaselineDatabaseTests.DatabaseFixture database)
    : IClassFixture<LocalDeltaBaselineDatabaseTests.DatabaseFixture>
{
    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-volume")]
    [InlineData("wrong-count")]
    [InlineData("invalid-digest")]
    [InlineData("reversed-cutoff")]
    [InlineData("future-reconciliation")]
    public async Task RealBaselineQueryGatesConnectionFileAndApply(string kind)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var reset = new NpgsqlCommand("TRUNCATE legacy_migration_internal.local_baseline_ready", connection))
            await reset.ExecuteNonQueryAsync();

        int rows = kind == "missing" ? 0 : kind == "duplicate" ? 2 : 1;
        for (int index = 0; index < rows; index++)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO legacy_migration_internal.local_baseline_ready
                VALUES ($1, $2, $3, $4, $5)
                """, connection);
            insert.Parameters.AddWithValue(kind == "wrong-volume" ? "different-owned-fixture" : "legacy-maliev-exact23-postgres-data");
            insert.Parameters.AddWithValue(kind == "wrong-count" ? 22 : 23);
            insert.Parameters.AddWithValue(kind == "invalid-digest" ? "invalid" : new string('a', 64));
            insert.Parameters.AddWithValue(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            insert.Parameters.AddWithValue(kind switch
            {
                "reversed-cutoff" => new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                "future-reconciliation" => new DateTime(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                _ => new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await insert.ExecuteNonQueryAsync();
        }

        string root = Path.Combine(Path.GetTempPath(), "maliev-owned-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "run", "connection.txt");
        string config = Path.Combine(root, "config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { delta = new { targetConnectionFile = target } }));
        LocalDeltaExecution.ProtectFile(config);
        bool applyCalled = false;
        try
        {
            Task<int> run = LocalDeltaExecution.RunAsync(name => name switch
            {
                "LEGACY_LOCAL_DELTA_CONFIG" => config,
                "ConnectionStrings__legacy-postgres-main" => database.ConnectionString,
                _ => null
            }, root, runMigration: (arguments, _, _, environment, _) =>
            {
                applyCalled = true;
                Assert.Equal(["apply-delta-local", "--config", config], arguments);
                Assert.Equal(database.ConnectionString, File.ReadAllText(target));
                Assert.Equal("apphost", environment("LEGACY_MIGRATION_CALLER"));
                Assert.Equal("false", environment("LEGACY_DEPLOY_ENABLED"));
                return Task.FromResult(17);
            });
            if (kind == "valid") Assert.Equal(17, await run);
            else
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
                Assert.Equal("The persistent PostgreSQL volume has no authenticated exact-23 baseline marker.", failure.Message);
            }
            Assert.Equal(kind == "valid", applyCalled);
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public sealed class DatabaseFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container = new PostgreSqlBuilder()
            .WithImage("postgres:18@sha256:4ef4dbc939d61acea57712655ddb4b4ab27419c913f94cca0cd57cb3ea3c2280")
            .WithDatabase("baseline_" + Guid.NewGuid().ToString("N"))
            .WithUsername("owned_fixture")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();

        public string ConnectionString => container.GetConnectionString();

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand("""
                CREATE SCHEMA legacy_migration_internal;
                CREATE TABLE legacy_migration_internal.local_baseline_ready (
                    volume_name text NOT NULL,
                    database_count integer NOT NULL,
                    baseline_evidence_sha256 text NOT NULL,
                    source_cutoff_utc timestamp with time zone NOT NULL,
                    reconciled_at_utc timestamp with time zone NOT NULL
                )
                """, connection);
            await create.ExecuteNonQueryAsync();
        }

        public Task DisposeAsync() => container.DisposeAsync().AsTask();
    }
}
