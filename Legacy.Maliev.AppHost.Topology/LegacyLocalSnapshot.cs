using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Consumes the credential-free encrypted snapshot contract emitted by Legacy.Maliev.DataMigration.</summary>
public sealed partial class LegacyLocalSnapshot
{
    public const int SupportedSchemaVersion = 2;
    public const string SupportedFormat = "MLVSNP02";
    public const string SupportedEncryption = "AES-256-GCM-chunked-v2";
    private static ReadOnlySpan<byte> Magic => "MLVSNP02"u8;
    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;

    private readonly string directoryPath;
    private readonly IReadOnlyDictionary<string, SnapshotEntry> entries;

    private LegacyLocalSnapshot(string directoryPath, IReadOnlyDictionary<string, SnapshotEntry> entries)
    {
        this.directoryPath = directoryPath;
        this.entries = entries;
    }

    public int DatabaseCount => entries.Count;

    public static LegacyLocalSnapshot Load(string directory, ReadOnlySpan<byte> rootKey, string expectedSnapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new InvalidOperationException($"Legacy local snapshot directory does not exist: {fullDirectory}");
        }

        string manifestPath = Path.Combine(fullDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("Legacy local snapshot manifest.json is missing.");
        }

        SnapshotManifest? manifest;
        try
        {
            using FileStream manifestStream = SecureSnapshotFile.OpenRead(manifestPath);
            if (manifestStream.Length is <= 0 or > 1024 * 1024)
                throw new InvalidOperationException("Legacy local snapshot manifest size is invalid.");
            manifest = JsonSerializer.Deserialize<SnapshotManifest>(manifestStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Legacy local snapshot manifest is not valid JSON.", exception);
        }

        if (manifest is null || manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidOperationException("Legacy local snapshot manifest schema version is unsupported.");
        }
        if (!string.Equals(manifest.Format, SupportedFormat, StringComparison.Ordinal) ||
            !string.Equals(manifest.Encryption, SupportedEncryption, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Legacy local snapshot encryption contract is unsupported.");
        }

        if (!SnapshotIdPattern().IsMatch(manifest.SnapshotId ?? string.Empty) ||
            !IsSha256(manifest.ManifestDigestSha256 ?? string.Empty) || !IsSha256(manifest.ManifestMacSha256 ?? string.Empty))
            throw new InvalidOperationException("Legacy local snapshot authenticated identity is invalid.");

        SnapshotEntry[] ordered = [.. (manifest.Databases ?? []).OrderBy(entry => entry.Database, StringComparer.Ordinal)];
        if (ordered.Length != LegacyTopology.DatabaseNames.Count ||
            !ordered.Select(entry => entry.Database).SequenceEqual(LegacyTopology.DatabaseNames, StringComparer.Ordinal) ||
            ordered.Select(entry => entry.Database).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new InvalidOperationException("Legacy local snapshot manifest must contain the exact 25 database inventory.");
        }

        string expectedMac = ComputeManifestMac(manifest with { Databases = ordered, ManifestMacSha256 = string.Empty }, rootKey);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedMac), Convert.FromHexString(manifest.ManifestMacSha256!)))
            throw new CryptographicException("Legacy local snapshot manifest authentication failed.");
        if (!string.Equals(manifest.SnapshotId, expectedSnapshotId, StringComparison.Ordinal))
            throw new CryptographicException("Legacy local snapshot identity is stale or does not match the requested run.");
        string digest = ComputeSemanticDigest(manifest.SnapshotId!, ordered);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(manifest.ManifestDigestSha256!)))
            throw new CryptographicException("Legacy local snapshot semantic manifest digest is invalid.");

        foreach (SnapshotEntry entry in ordered)
        {
            string expectedFileName = $"{entry.Database}.dump.aes256";
            if (!string.Equals(entry.FileName, expectedFileName, StringComparison.Ordinal) ||
                entry.FileName.IndexOfAny(['/', '\\']) >= 0 ||
                !ShadowName().IsMatch(entry.ShadowDatabase) ||
                entry.PlaintextByteLength <= 0 || entry.EncryptedByteLength <= 0 ||
                !IsSha256(entry.PlaintextSha256) || !IsSha256(entry.EncryptedSha256))
                throw new InvalidOperationException($"Legacy local snapshot manifest entry is invalid for '{entry.Database}'.");
        }

        return new LegacyLocalSnapshot(fullDirectory, ordered.ToDictionary(entry => entry.Database, StringComparer.Ordinal))
        { SnapshotId = manifest.SnapshotId!, ManifestDigestSha256 = manifest.ManifestDigestSha256! };
    }

    public string SnapshotId { get; private init; } = string.Empty;
    public string ManifestDigestSha256 { get; private init; } = string.Empty;

    public async Task<string> GetVerifiedEncryptedArchivePathAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        SnapshotEntry entry = GetEntry(databaseName);
        string path = Path.GetFullPath(Path.Combine(directoryPath, entry.FileName));
        if (!path.StartsWith(directoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new InvalidOperationException($"Legacy local snapshot encrypted archive is missing for database '{databaseName}'.");
        }
        await using FileStream stream = SecureSnapshotFile.OpenRead(path);
        if (stream.Length != entry.EncryptedByteLength)
        {
            throw new InvalidOperationException($"Legacy local snapshot ciphertext size does not match for database '{databaseName}'.");
        }
        string actualHash = await HashStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, entry.EncryptedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Legacy local snapshot ciphertext checksum does not match for database '{databaseName}'.");
        }
        return path;
    }

    public async Task RestoreVerifiedAsync(
        string databaseName,
        ReadOnlyMemory<byte> key,
        Func<Func<Stream, CancellationToken, Task>, CancellationToken, Task> restorePlaintext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(restorePlaintext);
        if (key.Length != 32)
        {
            throw new ArgumentException("Snapshot decryption requires a 256-bit key.", nameof(key));
        }
        SnapshotEntry entry = GetEntry(databaseName);
        string encryptedPath = Path.GetFullPath(Path.Combine(directoryPath, entry.FileName));
        await using var encrypted = SecureSnapshotFile.OpenRead(encryptedPath);
        if (encrypted.Length != entry.EncryptedByteLength ||
            !string.Equals(await HashStreamAsync(encrypted, cancellationToken).ConfigureAwait(false), entry.EncryptedSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException($"Encrypted snapshot integrity validation failed for database '{databaseName}'.");
        encrypted.Position = 0;

        var preAuthentication = new PlaintextIntegrityStream(Stream.Null);
        await DecryptAsync(encrypted, preAuthentication, key,
            new SnapshotArchiveContext(SnapshotId, databaseName, ManifestDigestSha256), cancellationToken)
            .ConfigureAwait(false);
        if (preAuthentication.Length != entry.PlaintextByteLength ||
            !string.Equals(preAuthentication.GetHash(), entry.PlaintextSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException($"Decrypted snapshot integrity validation failed for database '{databaseName}'.");
        }

        encrypted.Position = 0;
        await restorePlaintext(async (destination, restoreCancellationToken) =>
        {
            await DecryptAsync(encrypted, destination, key,
                new SnapshotArchiveContext(SnapshotId, databaseName, ManifestDigestSha256), restoreCancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(restoreCancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private SnapshotEntry GetEntry(string databaseName) => entries.TryGetValue(databaseName, out SnapshotEntry? entry)
        ? entry
        : throw new InvalidOperationException($"Legacy local snapshot is missing database '{databaseName}'.");

    private static async Task DecryptAsync(Stream encrypted, Stream plaintext, ReadOnlyMemory<byte> rootKey,
        SnapshotArchiveContext context, CancellationToken cancellationToken)
    {
        byte[] key = DeriveKey(rootKey.Span, "archive-encryption");
        byte[] magic = new byte[Magic.Length], baseNonce = new byte[8], header = new byte[4], cipher = new byte[ChunkSize],
            plain = new byte[ChunkSize], tag = new byte[TagSize], trailing = new byte[1];
        try
        {
            await ReadExactlyAsync(encrypted, magic, cancellationToken).ConfigureAwait(false);
            if (!magic.AsSpan().SequenceEqual(Magic)) throw new CryptographicException("The encrypted snapshot header is invalid.");
            await ReadExactlyAsync(encrypted, baseNonce, cancellationToken).ConfigureAwait(false);
            uint counter = 0;
            using var aes = new AesGcm(key, TagSize);
            while (true)
            {
                await ReadExactlyAsync(encrypted, header, cancellationToken).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length == 0)
                {
                    if (await encrypted.ReadAsync(trailing, cancellationToken).ConfigureAwait(false) != 0)
                        throw new CryptographicException("The encrypted snapshot contains trailing data.");
                    return;
                }
                if (length < 0 || length > ChunkSize || counter == uint.MaxValue)
                    throw new CryptographicException("The encrypted snapshot chunk is invalid.");
                await ReadExactlyAsync(encrypted, cipher.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                await ReadExactlyAsync(encrypted, tag, cancellationToken).ConfigureAwait(false);
                aes.Decrypt(CreateNonce(baseNonce, counter), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length),
                    CreateAssociatedData(context, counter, length));
                counter++;
                await plaintext.WriteAsync(plain.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(magic);
            CryptographicOperations.ZeroMemory(baseNonce); CryptographicOperations.ZeroMemory(header);
            CryptographicOperations.ZeroMemory(cipher); CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(tag); CryptographicOperations.ZeroMemory(trailing);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The encrypted snapshot is truncated.");
            total += read;
        }
    }

    private static byte[] CreateNonce(byte[] baseNonce, uint counter)
    {
        byte[] nonce = new byte[12];
        baseNonce.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private static byte[] CreateAssociatedData(SnapshotArchiveContext context, uint counter, int length)
    {
        byte[] identityHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{context.SnapshotId}\n{context.Database}\n{context.ManifestDigestSha256}"));
        byte[] associated = new byte[48];
        Magic.CopyTo(associated);
        BinaryPrimitives.WriteUInt32BigEndian(associated.AsSpan(8), counter);
        BinaryPrimitives.WriteInt32BigEndian(associated.AsSpan(12), length);
        identityHash.CopyTo(associated, 16);
        return associated;
    }

    private static async Task<string> HashStreamAsync(Stream stream, CancellationToken cancellationToken)
        => Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);


    private sealed class PlaintextIntegrityStream(Stream destination) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public override long Length => bytesWritten;
        public string GetHash() => Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Position { get => Length; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        private long bytesWritten;
        public override void Write(byte[] buffer, int offset, int count) { hash.AppendData(buffer, offset, count); bytesWritten += count; destination.Write(buffer, offset, count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { hash.AppendData(buffer.Span); bytesWritten += buffer.Length; await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> rootKey, string purpose)
    {
        if (rootKey.Length != 32) throw new ArgumentException("Snapshot root key must contain exactly 32 bytes.");
        byte[] input = rootKey.ToArray();
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, input, 32, "MALIEV-Legacy-Snapshot-v2"u8.ToArray(), Encoding.UTF8.GetBytes(purpose)); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    private static string ComputeSemanticDigest(string snapshotId, IReadOnlyList<SnapshotEntry> databases)
        => Convert.ToHexString(SHA256.HashData(WriteCanonical(snapshotId, databases, authenticated: false, null))).ToLowerInvariant();

    private static string ComputeManifestMac(SnapshotManifest manifest, ReadOnlySpan<byte> rootKey)
    {
        byte[] key = DeriveKey(rootKey, "manifest-authentication");
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(key,
            WriteCanonical(manifest.SnapshotId!, manifest.Databases!, authenticated: true, manifest))).ToLowerInvariant();
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] WriteCanonical(string snapshotId, IReadOnlyList<SnapshotEntry> databases, bool authenticated, SnapshotManifest? manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteString("format", SupportedFormat);
            if (authenticated) writer.WriteString("encryption", SupportedEncryption);
            writer.WriteString("snapshotId", snapshotId);
            if (authenticated) writer.WriteString("manifestDigestSha256", manifest!.ManifestDigestSha256);
            writer.WriteStartArray("databases");
            foreach (SnapshotEntry entry in databases.OrderBy(x => x.Database, StringComparer.Ordinal))
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

    [GeneratedRegex("^legacy_shadow_[a-z0-9_]+_[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShadowName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotIdPattern();

    public sealed record SnapshotManifest(int SchemaVersion, string? Format, string Encryption, string? SnapshotId,
        string? ManifestDigestSha256, string? ManifestMacSha256, IReadOnlyList<SnapshotEntry>? Databases);
    public sealed record SnapshotEntry(string Database, string ShadowDatabase, string FileName, long PlaintextByteLength,
        string PlaintextSha256, long EncryptedByteLength, string EncryptedSha256);
}

internal sealed record SnapshotArchiveContext(string SnapshotId, string Database, string ManifestDigestSha256);

/// <summary>Loads snapshot key material from a caller-owned path without exposing it in process arguments or manifests.</summary>
public static class SnapshotEncryptionKey
{
    public static byte[] Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
            throw new InvalidOperationException("The snapshot encryption key file is missing or unsafe.");

        using var stream = SecureSnapshotFile.OpenRead(fullPath, exclusive: true);
        if (stream.Length is <= 0 or > 4096)
            throw new InvalidOperationException("The snapshot encryption key file has an invalid size.");
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        char[] characters = new char[4096];
        byte[] key = new byte[32];
        try
        {
            int count = reader.ReadBlock(characters, 0, characters.Length);
            int start = 0, end = count;
            while (start < end && char.IsWhiteSpace(characters[start])) start++;
            while (end > start && char.IsWhiteSpace(characters[end - 1])) end--;
            if (!Convert.TryFromBase64Chars(characters.AsSpan(start, end - start), key, out int written) || written != 32)
                throw new InvalidOperationException("The snapshot encryption key file is not valid base64 or is not 32 bytes.");
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally { Array.Clear(characters); }
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("The snapshot encryption key must contain exactly 32 bytes.");
        }
        return key;
    }

    private static void ValidateOpenedHandle(SafeFileHandle handle, string expectedPath)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateOpenedHandleWindows(handle, expectedPath);
            return;
        }

        string descriptorPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
        FileSystemInfo? resolved = new FileInfo(descriptorPath).ResolveLinkTarget(returnFinalTarget: true);
        if (resolved is null || !string.Equals(Path.GetFullPath(resolved.FullName), expectedPath, StringComparison.Ordinal))
            throw new InvalidOperationException("The snapshot encryption key opened-handle identity is unsafe.");
    }

    private static void ValidateOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            ValidateOwnerOnlyPermissionsWindows(path);
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if (!mode.HasFlag(UnixFileMode.UserRead) || (mode & forbidden) != 0)
            throw new InvalidOperationException("The snapshot encryption key file must have owner-only permissions.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ValidateOpenedHandleWindows(SafeFileHandle handle, string expectedPath)
    {
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new InvalidOperationException("The snapshot encryption key opened-handle identity cannot be verified.");
        string openedPath = NormalizeWindowsHandlePath(buffer.ToString());
        if (!string.Equals(Path.GetFullPath(openedPath), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The snapshot encryption key opened-handle identity is unsafe.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ValidateOwnerOnlyPermissionsWindows(string path)
    {
        SecurityIdentifier currentOwner = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("The current Windows identity has no security identifier.");
        FileSecurity security = new FileInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != currentOwner)
            throw new InvalidOperationException("The snapshot encryption key file must be owned by the current user.");
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules.OfType<FileSystemAccessRule>())
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && sid != owner)
                throw new InvalidOperationException("The snapshot encryption key file must have owner-only permissions.");
        }
    }

    private static string NormalizeWindowsHandlePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase) ? path[devicePrefix.Length..] : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);
}
