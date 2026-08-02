using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyLocalSnapshotTests
{
    [Fact]
    public void Load_ValidatesArchiveSizeAndChecksum()
    {
        using var directory = new TemporaryDirectory();
        var archive = Encoding.UTF8.GetBytes("local dump fixture");
        File.WriteAllBytes(Path.Combine(directory.Path, "Country.dump"), archive);
        WriteManifest(directory.Path, "Country", "Country.dump", archive);

        var snapshot = LegacyLocalSnapshot.Load(directory.Path);

        Assert.Equal(
            Path.Combine(directory.Path, "Country.dump"),
            snapshot.GetArchivePath("Country"));
    }

    [Fact]
    public void GetArchivePath_RejectsChecksumDrift()
    {
        using var directory = new TemporaryDirectory();
        var archive = Encoding.UTF8.GetBytes("local dump fixture");
        File.WriteAllBytes(Path.Combine(directory.Path, "Country.dump"), archive);
        WriteManifest(directory.Path, "Country", "Country.dump", archive);
        File.AppendAllText(Path.Combine(directory.Path, "Country.dump"), " changed");

        var snapshot = LegacyLocalSnapshot.Load(directory.Path);

        var message = Assert.Throws<InvalidOperationException>(
            () => snapshot.GetArchivePath("Country")).Message;
        Assert.True(
            message.Contains("size", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("checksum", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_RejectsArchivePathTraversal()
    {
        using var directory = new TemporaryDirectory();
        var archive = Encoding.UTF8.GetBytes("local dump fixture");
        WriteManifest(directory.Path, "Country", "..\\Country.dump", archive);

        Assert.Contains("entry is invalid", Assert.Throws<InvalidOperationException>(
            () => LegacyLocalSnapshot.Load(directory.Path)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetArchivePath_RequiresEveryDatabaseUsedByTheRuntime()
    {
        using var directory = new TemporaryDirectory();
        var archive = Encoding.UTF8.GetBytes("local dump fixture");
        File.WriteAllBytes(Path.Combine(directory.Path, "Country.dump"), archive);
        WriteManifest(directory.Path, "Country", "Country.dump", archive);
        var snapshot = LegacyLocalSnapshot.Load(directory.Path);

        Assert.Contains("EmployeeIdentity", Assert.Throws<InvalidOperationException>(
            () => snapshot.GetArchivePath("EmployeeIdentity")).Message, StringComparison.Ordinal);
    }

    private static void WriteManifest(string directory, string database, string file, byte[] archive)
    {
        var manifest = new
        {
            format = LegacyLocalSnapshot.ManifestFormat,
            databaseCount = 1,
            databases = new[]
            {
                new
                {
                    database,
                    file,
                    bytes = archive.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(),
                },
            },
        };
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "maliev-legacy-snapshot-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
