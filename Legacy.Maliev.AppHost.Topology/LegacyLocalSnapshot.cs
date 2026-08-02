using System.Security.Cryptography;
using System.Text.Json;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>
/// Describes a read-only PostgreSQL snapshot captured from the migrated legacy
/// database cluster. Snapshot files are deliberately kept outside the
/// repository and are never treated as application configuration.
/// </summary>
public sealed class LegacyLocalSnapshot
{
    public const string ManifestFormat = "MALIEV legacy PostgreSQL local snapshot v1";

    private readonly string directoryPath;
    private readonly IReadOnlyDictionary<string, SnapshotEntry> entries;

    private LegacyLocalSnapshot(string directoryPath, IReadOnlyDictionary<string, SnapshotEntry> entries)
    {
        this.directoryPath = directoryPath;
        this.entries = entries;
    }

    public static LegacyLocalSnapshot Load(string directory)
    {
        var fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new InvalidOperationException($"Legacy local snapshot directory does not exist: {fullDirectory}");
        }

        var manifestPath = Path.Combine(fullDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("Legacy local snapshot manifest.json is missing.");
        }

        SnapshotManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SnapshotManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Legacy local snapshot manifest is not valid JSON.", exception);
        }

        if (manifest is null || !string.Equals(manifest.Format, ManifestFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Legacy local snapshot manifest format is unsupported.");
        }

        var entries = (manifest.Databases ?? [])
            .ToDictionary(
                static entry => entry.Database,
                static entry => entry,
                StringComparer.Ordinal);

        if (entries.Count != manifest.DatabaseCount || entries.Keys.Any(static name => !LegacyTopology.DatabaseNames.Contains(name)))
        {
            throw new InvalidOperationException("Legacy local snapshot manifest database inventory is invalid.");
        }

        foreach (var entry in entries.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.Database) ||
                string.IsNullOrWhiteSpace(entry.File) ||
                !string.Equals(Path.GetFileName(entry.File), entry.File, StringComparison.Ordinal) ||
                !IsSha256(entry.Sha256) ||
                entry.Bytes <= 0)
            {
                throw new InvalidOperationException($"Legacy local snapshot manifest entry is invalid for '{entry.Database}'.");
            }
        }

        return new(fullDirectory, entries);
    }

    public string GetArchivePath(string databaseName)
    {
        if (!entries.TryGetValue(databaseName, out var entry))
        {
            throw new InvalidOperationException($"Legacy local snapshot is missing database '{databaseName}'.");
        }

        var path = Path.Combine(directoryPath, entry.File);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Legacy local snapshot archive is missing for database '{databaseName}'.");
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length != entry.Bytes)
        {
            throw new InvalidOperationException($"Legacy local snapshot archive size does not match the manifest for database '{databaseName}'.");
        }

        var actualSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        if (!string.Equals(actualSha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Legacy local snapshot archive checksum does not match the manifest for database '{databaseName}'.");
        }

        return path;
    }

    public sealed record SnapshotManifest(
        string Format,
        int DatabaseCount,
        IReadOnlyList<SnapshotEntry>? Databases);

    public sealed record SnapshotEntry(
        string Database,
        string File,
        long Bytes,
        string Sha256);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character => Uri.IsHexDigit(character));
}
