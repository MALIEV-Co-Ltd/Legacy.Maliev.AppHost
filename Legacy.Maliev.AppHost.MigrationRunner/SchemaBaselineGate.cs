using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace Legacy.Maliev.AppHost.MigrationRunner;

/// <summary>
/// The evidence required before a migration runner may touch a database that already contains
/// application tables. The receipt is created from an approved source backup and the live target
/// schema fingerprint; it is deliberately not inferred by the migration runner.
/// </summary>
public sealed record SchemaBaselineReceipt(
    [property: JsonPropertyName("workload")] string Workload,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("schemaFingerprint")] string SchemaFingerprint,
    [property: JsonPropertyName("sourceBackupId")] string SourceBackupId,
    [property: JsonPropertyName("capturedAtUtc")] DateTimeOffset CapturedAtUtc);

/// <summary>
/// A normalized column descriptor used to create deterministic schema fingerprints.
/// </summary>
public readonly record struct SchemaColumn(
    string Schema,
    string Table,
    string Column,
    int Ordinal,
    string DataType,
    string UdtName,
    string IsNullable,
    string MaximumLength,
    string NumericPrecision,
    string NumericScale,
    string DatetimePrecision,
    string DefaultValue);

public static class SchemaBaselineGate
{
    public const string BaselineReceiptEnvironmentVariable = "LEGACY_SCHEMA_BASELINE_RECEIPT";
    public const string LocalOverrideEnvironmentVariable = "LEGACY_LOCAL_ALLOW_NONEMPTY_MIGRATE";

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task EnsureSafeToMigrateAsync(
        string workload,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        if (IsLocalOverride(Environment.GetEnvironmentVariable(LocalOverrideEnvironmentVariable)))
        {
            Console.WriteLine(
                $"{LocalOverrideEnvironmentVariable}=true; allowing the local Aspire migration for '{workload}'.");
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        if (!await HasUserTablesAsync(connection, cancellationToken))
        {
            // A new database is safe to initialize. The receipt is only required for copied
            // databases where a migration could change or seed production-derived data.
            return;
        }

        var receiptPath = Environment.GetEnvironmentVariable(BaselineReceiptEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(receiptPath))
        {
            throw new InvalidOperationException(
                $"Database '{connection.Database}' is non-empty for migration workload '{workload}'. "
                + $"Set {BaselineReceiptEnvironmentVariable} to an approved source-backed schema receipt "
                + "or restore an empty database. No migrations or seed operations were run.");
        }

        var receipt = ReadReceipt(receiptPath);
        var actualFingerprint = await ComputeSchemaFingerprintAsync(connection, cancellationToken);
        ValidateReceipt(receipt, workload, connection.Database, actualFingerprint);
    }

    public static bool IsLocalOverride(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    public static SchemaBaselineReceipt ReadReceipt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var receipt = JsonSerializer.Deserialize<SchemaBaselineReceipt>(
                File.ReadAllText(path),
                ReceiptJsonOptions);

            if (receipt is null)
            {
                throw new InvalidOperationException("The schema baseline receipt is empty.");
            }

            ValidateReceiptShape(receipt);
            return receipt;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The schema baseline receipt '{path}' is not valid JSON or has an unsupported field.",
                exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The schema baseline receipt '{path}' could not be read.",
                exception);
        }
    }

    public static void ValidateReceipt(
        SchemaBaselineReceipt receipt,
        string workload,
        string database,
        string actualFingerprint)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualFingerprint);

        ValidateReceiptShape(receipt);

        if (!string.Equals(receipt.Workload, workload, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The schema baseline receipt workload '{receipt.Workload}' does not match '{workload}'. "
                + "No migrations or seed operations were run.");
        }

        if (!string.Equals(receipt.Database, database, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The schema baseline receipt database '{receipt.Database}' does not match '{database}'. "
                + "No migrations or seed operations were run.");
        }

        if (!string.Equals(receipt.SchemaFingerprint, actualFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The schema baseline receipt does not match the live target schema. "
                + "Refresh the source-backed receipt after investigating drift; no migrations or seed operations were run.");
        }
    }

    public static string ComputeSchemaFingerprint(IEnumerable<SchemaColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var canonical = string.Join(
            '\n',
            columns
                .OrderBy(column => column.Schema, StringComparer.Ordinal)
                .ThenBy(column => column.Table, StringComparer.Ordinal)
                .ThenBy(column => column.Ordinal)
                .ThenBy(column => column.Column, StringComparer.Ordinal)
                .Select(column => string.Join(
                    '|',
                    Escape(column.Schema),
                    Escape(column.Table),
                    Escape(column.Column),
                    column.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Escape(column.DataType),
                    Escape(column.UdtName),
                    Escape(column.IsNullable),
                    Escape(column.MaximumLength),
                    Escape(column.NumericPrecision),
                    Escape(column.NumericScale),
                    Escape(column.DatetimePrecision),
                    Escape(column.DefaultValue))));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task<bool> HasUserTablesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
                  AND table_type = 'BASE TABLE'
                  AND table_name <> '__EFMigrationsHistory'
            );
            """,
            connection);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<string> ComputeSchemaFingerprintAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT table_schema,
                   table_name,
                   column_name,
                   ordinal_position,
                   data_type,
                   udt_name,
                   is_nullable,
                   COALESCE(character_maximum_length::text, ''),
                   COALESCE(numeric_precision::text, ''),
                   COALESCE(numeric_scale::text, ''),
                   COALESCE(datetime_precision::text, ''),
                   COALESCE(column_default, '')
            FROM information_schema.columns
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name, ordinal_position;
            """,
            connection);

        var columns = new List<SchemaColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(
                new SchemaColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(11)));
        }

        return ComputeSchemaFingerprint(columns);
    }

    private static void ValidateReceiptShape(SchemaBaselineReceipt receipt)
    {
        if (string.IsNullOrWhiteSpace(receipt.Workload)
            || string.IsNullOrWhiteSpace(receipt.Database)
            || string.IsNullOrWhiteSpace(receipt.SchemaFingerprint)
            || string.IsNullOrWhiteSpace(receipt.SourceBackupId)
            || receipt.CapturedAtUtc == default)
        {
            throw new InvalidOperationException(
                "The schema baseline receipt must contain workload, database, schemaFingerprint, sourceBackupId, and capturedAtUtc.");
        }

        if (receipt.SchemaFingerprint.Length != 64
            || receipt.SchemaFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("The schema baseline receipt fingerprint must be a SHA-256 hex value.");
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace('|', '_').Replace('\n', '_');
}
