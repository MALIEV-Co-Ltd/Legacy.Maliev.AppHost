using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Legacy.Maliev.AppHost.MigrationRunner;
using Legacy.Maliev.AppHost.Topology;
using Npgsql;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyLocalSnapshotTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"maliev-snapshot-consumer-{Guid.NewGuid():N}");

    [Fact]
    public async Task Load_AcceptsProducerExactTwentyFiveEncryptedManifest()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        Assert.Equal(25, snapshot.DatabaseCount);
        Assert.EndsWith("Country.dump.aes256", await snapshot.GetVerifiedEncryptedArchivePathAsync("Country"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_RejectsManifestMissingOneDatabase()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key, LegacyTopology.DatabaseNames.Take(LegacyTopology.DatabaseNames.Count - 1));
        Assert.Contains("exact 25", Assert.Throws<InvalidOperationException>(() => LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830")).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_RejectsManifestEntryRemapBeforeArchiveUse()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        string path = Path.Combine(root, "manifest.json");
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("Customer.dump.aes256", "Employee.dump.aes256", StringComparison.Ordinal));
        Assert.ThrowsAny<CryptographicException>(() => LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830"));
    }

    [Fact]
    public async Task Load_RejectsManifestFromDifferentRootKey()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        Assert.Throws<CryptographicException>(() => LegacyLocalSnapshot.Load(root, RandomNumberGenerator.GetBytes(32), "test-snapshot-20260830"));

    }

    [Fact]
    public async Task Load_RejectsAuthenticatedButStaleSnapshotIdentity()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        Assert.Throws<CryptographicException>(() => LegacyLocalSnapshot.Load(root, key, "different-run"));
    }

    [Fact]
    public void Load_RejectsLegacyPlaintextManifest()
    {
        Directory.CreateDirectory(root);
        string manifestPath = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifestPath, """{"format":"MALIEV legacy PostgreSQL local snapshot v1","databaseCount":25,"databases":[]}""");
        RestrictKeyFile(manifestPath);
        Assert.Contains("schema version", Assert.Throws<InvalidOperationException>(() => LegacyLocalSnapshot.Load(root, new byte[32], "test-snapshot-20260830")).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetVerifiedEncryptedArchivePathAsync_RejectsCiphertextDrift()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        File.AppendAllText(Path.Combine(root, "Country.dump.aes256"), "tampered");
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.GetVerifiedEncryptedArchivePathAsync("Country"));
    }

    [Fact]
    public async Task RestoreVerifiedAsync_StreamsProducerFixtureWithoutPlaintextFile()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        IReadOnlyDictionary<string, byte[]> dumps = await WriteProducerFixtureAsync(key);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        byte[]? observedBytes = null;
        await snapshot.RestoreVerifiedAsync("Country", key, async (writeArchive, cancellationToken) =>
        {
            await using var destination = new MemoryStream();
            await writeArchive(destination, cancellationToken);
            observedBytes = destination.ToArray();
        }, CancellationToken.None);
        Assert.Equal(dumps["Country"], observedBytes);
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "maliev-snapshot-restore-*"));
    }

    [Fact]
    public async Task RestoreVerifiedAsync_WrongKeyNeverInvokesRestoreAndCleansPlaintext()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        var invoked = false;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => snapshot.RestoreVerifiedAsync(
            "Country", RandomNumberGenerator.GetBytes(32), async (writeArchive, token) =>
            {
                invoked = true;
                await writeArchive(Stream.Null, token);
            }, CancellationToken.None));
        Assert.False(invoked);
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "maliev-snapshot-restore-*"));
    }

    [Fact]
    public async Task RestoreVerifiedAsync_ArchiveFromDifferentSnapshotIsRejectedBeforeRestore()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        IReadOnlyDictionary<string, byte[]> dumps = await WriteProducerFixtureAsync(key);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        string countryPath = Path.Combine(root, "Country.dump.aes256");
        File.Delete(countryPath);
        await EncryptLikeProducerAsync(dumps["Country"], countryPath, key, "different-snapshot", "Country",
            snapshot.ManifestDigestSha256);
        RestrictKeyFile(countryPath);
        var invoked = false;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => snapshot.RestoreVerifiedAsync("Country", key, async (writeArchive, token) =>
        {
            invoked = true;
            await writeArchive(Stream.Null, token);
        }, CancellationToken.None));
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreVerifiedAsync_CiphertextTamperOrTruncationNeverInvokesRestore(bool truncate)
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        string path = Path.Combine(root, "Country.dump.aes256");
        byte[] ciphertext = await File.ReadAllBytesAsync(path);
        if (truncate) Array.Resize(ref ciphertext, ciphertext.Length - 1);
        else ciphertext[^8] ^= 0x40;
        await File.WriteAllBytesAsync(path, ciphertext);
        RestrictKeyFile(path);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        var invoked = false;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => snapshot.RestoreVerifiedAsync("Country", key, (_, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestoreVerifiedAsync_PlaintextLengthOrHashMismatchNeverInvokesRestore(bool mismatchLength)
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key, plaintextMetadata: (_, length, hash) =>
            mismatchLength ? (length + 1, hash) : (length, new string('0', 64)));
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        var invoked = false;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => snapshot.RestoreVerifiedAsync("Country", key, (_, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.False(invoked);
    }

    [Fact]
    public async Task RestoreVerifiedAsync_OverPermissiveCiphertextIsRejected()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        string path = Path.Combine(root, "Country.dump.aes256");
        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new FileInfo(path).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.RestoreVerifiedAsync(
            "Country", key, async (writeArchive, token) => await writeArchive(Stream.Null, token), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreVerifiedAsync_RestoreFailureOrCancellationLeavesNoPlaintext(bool cancel)
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key);
        var snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        async Task Restore(Func<Stream, CancellationToken, Task> writeArchive, CancellationToken token)
        {
            await writeArchive(Stream.Null, token);
            if (cancel) throw new OperationCanceledException(new CancellationToken(canceled: true));
            throw new InvalidOperationException("restore failed");
        }
        await Assert.ThrowsAnyAsync<Exception>(() => snapshot.RestoreVerifiedAsync("Country", key, Restore, CancellationToken.None));
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "maliev-snapshot-restore-*"));
    }

    [Fact]
    public void SnapshotEncryptionKey_LoadsBase64KeyOnlyFromReferencedFile()
    {
        Directory.CreateDirectory(root);
        string keyPath = Path.Combine(root, "snapshot.key");
        byte[] expected = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(keyPath, Convert.ToBase64String(expected));
        RestrictKeyFile(keyPath);
        Assert.Equal(expected, SnapshotEncryptionKey.Load(keyPath));
    }

    [Fact]
    public void SnapshotEncryptionKey_RejectsKeyFileReadableByAnotherPrincipal()
    {
        Directory.CreateDirectory(root);
        string keyPath = Path.Combine(root, "over-permissive.key");
        File.WriteAllText(keyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(keyPath).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        Assert.Contains("unsafe", Assert.Throws<InvalidOperationException>(() => SnapshotEncryptionKey.Load(keyPath)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSql18SnapshotConsumerIntegrationFact]
    public async Task ProducerCustomArchive_RestoresSchemaAndRowsThroughProductionStreamingConsumer()
    {
        string archivePath = RequiredIntegrationEnvironment("LEGACY_SNAPSHOT_INTEGRATION_CUSTOM_ARCHIVE");
        string pgRestore = RequiredIntegrationEnvironment("PG_RESTORE_PATH");
        string administrativeConnection = RequiredIntegrationEnvironment("LEGACY_SNAPSHOT_INTEGRATION_RESTORE_CONNECTION");
        AssertPostgreSql18Tool(pgRestore);
        byte[] customArchive = await File.ReadAllBytesAsync(archivePath);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        await WriteProducerFixtureAsync(key, overrides: new Dictionary<string, byte[]> { ["Country"] = customArchive });
        LegacyLocalSnapshot snapshot = LegacyLocalSnapshot.Load(root, key, "test-snapshot-20260830");
        string restoredDatabase = $"legacy_snapshot_consumer_{Guid.NewGuid():N}";
        try
        {
            await ExecuteAdministrativeCommandAsync(administrativeConnection, $"CREATE DATABASE \"{restoredDatabase}\"");
            var target = new NpgsqlConnectionStringBuilder(administrativeConnection) { Database = restoredDatabase };
            await snapshot.RestoreVerifiedAsync("Country", key,
                (writeArchive, token) => PgRestoreRunner.RunPgRestoreAsync(writeArchive, "Country", target.ConnectionString, token),
                CancellationToken.None);

            await using var restored = new NpgsqlConnection(target.ConnectionString);
            await restored.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT value FROM snapshot_probe WHERE id = 1", restored);
            Assert.Equal("pg18", await query.ExecuteScalarAsync());
        }
        finally
        {
            await ExecuteAdministrativeCommandAsync(administrativeConnection,
                $"DROP DATABASE IF EXISTS \"{restoredDatabase}\" WITH (FORCE)");
        }
    }

    private static async Task ExecuteAdministrativeCommandAsync(string connectionString, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static string RequiredIntegrationEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{name} is required when PostgreSQL 18 snapshot consumer integration is enabled.");
    }

    private static void AssertPostgreSql18Tool(string executable)
    {
        var start = new ProcessStartInfo(executable, "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("pg_restore did not start.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("18.", output, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> WriteProducerFixtureAsync(byte[] key,
        IEnumerable<string>? names = null, IReadOnlyDictionary<string, byte[]>? overrides = null,
        Func<string, long, string, (long Length, string Hash)>? plaintextMetadata = null)
    {
        Directory.CreateDirectory(root);
        var dumps = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var entries = new List<FixtureEntry>();
        foreach (string database in names ?? LegacyTopology.DatabaseNames)
        {
            byte[] plaintext = overrides is not null && overrides.TryGetValue(database, out byte[]? replacement)
                ? replacement : Encoding.UTF8.GetBytes($"pg-dump:{database}:{new string('x', 129)}");
            dumps.Add(database, plaintext);
            string fileName = $"{database}.dump.aes256";
            string plaintextHash = HexSha256(plaintext);
            (long length, string hash) = plaintextMetadata?.Invoke(database, plaintext.LongLength, plaintextHash)
                ?? (plaintext.LongLength, plaintextHash);
            entries.Add(new(database, $"legacy_shadow_{database.ToLowerInvariant()}_{Guid.NewGuid():N}", fileName,
                length, hash, 0, string.Empty));
        }
        const string snapshotId = "test-snapshot-20260830";
        string digest = HexSha256(Canonical(snapshotId, entries, authenticated: false, null));
        for (int index = 0; index < entries.Count; index++)
        {
            FixtureEntry entry = entries[index];
            string filePath = Path.Combine(root, entry.FileName);
            await EncryptLikeProducerAsync(dumps[entry.Database], filePath, key, snapshotId, entry.Database, digest);
            byte[] encrypted = await File.ReadAllBytesAsync(filePath);
            entries[index] = entry with { EncryptedByteLength = encrypted.LongLength, EncryptedSha256 = HexSha256(encrypted) };
            RestrictKeyFile(filePath);
        }
        var unsigned = new FixtureManifest(2, "MLVSNP02", "AES-256-GCM-chunked-v2", snapshotId, digest, string.Empty, entries);
        byte[] macKey = DeriveKey(key, "manifest-authentication");
        string mac;
        try { mac = Convert.ToHexString(HMACSHA256.HashData(macKey, Canonical(snapshotId, entries, true, unsigned))).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(macKey); }
        string manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            format = "MLVSNP02",
            encryption = "AES-256-GCM-chunked-v2",
            snapshotId,
            manifestDigestSha256 = digest,
            manifestMacSha256 = mac,
            databases = entries
        }));
        RestrictKeyFile(manifestPath);
        return dumps;
    }

    private static async Task EncryptLikeProducerAsync(byte[] plaintext, string path, byte[] rootKey,
        string snapshotId, string database, string manifestDigest)
    {
        byte[] magic = "MLVSNP02"u8.ToArray();
        byte[] baseNonce = RandomNumberGenerator.GetBytes(8);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await output.WriteAsync(magic);
        await output.WriteAsync(baseNonce);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, plaintext.Length);
        await output.WriteAsync(header);
        byte[] nonce = new byte[12];
        baseNonce.CopyTo(nonce, 0);
        byte[] associated = new byte[48];
        magic.CopyTo(associated, 0);
        BinaryPrimitives.WriteInt32BigEndian(associated.AsSpan(12), plaintext.Length);
        SHA256.HashData(Encoding.UTF8.GetBytes($"{snapshotId}\n{database}\n{manifestDigest}")).CopyTo(associated, 16);
        byte[] cipher = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        byte[] key = DeriveKey(rootKey, "archive-encryption");
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintext, cipher, tag, associated); }
        finally { CryptographicOperations.ZeroMemory(key); }
        await output.WriteAsync(cipher);
        await output.WriteAsync(tag);
        BinaryPrimitives.WriteInt32BigEndian(header, 0);
        await output.WriteAsync(header);
    }

    private static byte[] DeriveKey(byte[] rootKey, string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256,
        rootKey, 32, "MALIEV-Legacy-Snapshot-v2"u8.ToArray(), Encoding.UTF8.GetBytes(purpose));

    private static byte[] Canonical(string snapshotId, IReadOnlyList<FixtureEntry> entries, bool authenticated, FixtureManifest? manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteString("format", "MLVSNP02");
            if (authenticated) writer.WriteString("encryption", "AES-256-GCM-chunked-v2");
            writer.WriteString("snapshotId", snapshotId);
            if (authenticated) writer.WriteString("manifestDigestSha256", manifest!.ManifestDigestSha256);
            writer.WriteStartArray("databases");
            foreach (FixtureEntry entry in entries.OrderBy(x => x.Database, StringComparer.Ordinal))
            {
                writer.WriteStartObject(); writer.WriteString("database", entry.Database); writer.WriteString("shadowDatabase", entry.ShadowDatabase);
                writer.WriteString("fileName", entry.FileName); writer.WriteNumber("plaintextByteLength", entry.PlaintextByteLength);
                writer.WriteString("plaintextSha256", entry.PlaintextSha256);
                if (authenticated) { writer.WriteNumber("encryptedByteLength", entry.EncryptedByteLength); writer.WriteString("encryptedSha256", entry.EncryptedSha256); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private sealed record FixtureEntry(string Database, string ShadowDatabase, string FileName, long PlaintextByteLength,
        string PlaintextSha256, long EncryptedByteLength, string EncryptedSha256);
    private sealed record FixtureManifest(int SchemaVersion, string Format, string Encryption, string SnapshotId,
        string ManifestDigestSha256, string ManifestMacSha256, IReadOnlyList<FixtureEntry> Databases);

    private static string HexSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void RestrictKeyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

public sealed class PostgreSql18SnapshotConsumerIntegrationFactAttribute : FactAttribute
{
    public PostgreSql18SnapshotConsumerIntegrationFactAttribute()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("MALIEV_RUN_PG18_SNAPSHOT_INTEGRATION"),
            "1",
            StringComparison.Ordinal))
        {
            Skip = "PostgreSQL 18 snapshot consumer compatibility is explicitly gated: set MALIEV_RUN_PG18_SNAPSHOT_INTEGRATION=1 and configure the archive and pg_restore prerequisites.";
        }
    }
}
