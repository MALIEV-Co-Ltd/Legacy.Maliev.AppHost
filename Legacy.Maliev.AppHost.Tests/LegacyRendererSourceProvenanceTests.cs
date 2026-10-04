using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyRendererSourceProvenanceTests
{
    private static string AssemblyPath => typeof(LegacyEdgeReviewPackage).Assembly.Location;
    private static string PdbPath => Path.ChangeExtension(AssemblyPath, ".pdb");
    // Deriving the hash is appropriate for these correspondence unit tests only.
    // The external consumer requires an independently reviewed artifact hash.
    private static string ArtifactHash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(AssemblyPath)));

    [Fact]
    public void Validate_AcceptsLinkedPdbAndCurrentRendererSource() =>
        LegacyRendererSourceProvenance.Validate(AssemblyPath, PdbPath, RendererSourcePath(), ArtifactHash);

    [Fact]
    public void Validate_RejectsChangedRendererSource()
    {
        var evidenceDirectory = Path.Combine(Path.GetTempPath(), "apphost-renderer-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidenceDirectory);
        var sourceCopy = Path.Combine(evidenceDirectory, "LegacyEdgeReviewPackage.cs");
        File.WriteAllText(sourceCopy, File.ReadAllText(RendererSourcePath()) + "\n// changed isolated test source\n");
        Assert.Throws<InvalidDataException>(() => LegacyRendererSourceProvenance.Validate(AssemblyPath, PdbPath, sourceCopy, ArtifactHash));
    }

    [Fact]
    public void Validate_RejectsWrongArtifactHash() =>
        Assert.Throws<InvalidDataException>(() => LegacyRendererSourceProvenance.Validate(AssemblyPath, PdbPath, RendererSourcePath(), new string('0', 64)));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("FFFFFFFF")]
    public void Validate_RejectsMalformedArtifactHash(string hash) =>
        Assert.Throws<ArgumentException>(() => LegacyRendererSourceProvenance.Validate(AssemblyPath, PdbPath, RendererSourcePath(), hash));

    [Fact]
    public void Validate_RejectsMissingPdb() =>
        Assert.Throws<FileNotFoundException>(() => LegacyRendererSourceProvenance.Validate(AssemblyPath, PdbPath + ".missing", RendererSourcePath(), ArtifactHash));

    [Fact]
    public void Validate_RejectsUnrelatedPortablePdb()
    {
        var foreignPdb = Path.ChangeExtension(typeof(LegacyRendererSourceProvenanceTests).Assembly.Location, ".pdb");
        Assert.Throws<InvalidDataException>(() => LegacyRendererSourceProvenance.Validate(AssemblyPath, foreignPdb, RendererSourcePath(), ArtifactHash));
    }

    private static string RendererSourcePath([CallerFilePath] string testSourcePath = "")
    {
        // CI can map compiler document paths; locate the actual test checkout first.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
            {
                var candidate = Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.Topology", "LegacyEdgeReviewPackage.cs");
                if (File.Exists(candidate)) { return candidate; }
                throw new FileNotFoundException("Renderer source is missing from the test checkout.", candidate);
            }
            directory = directory.Parent;
        }

        // A linked isolated harness has no AppHost solution ancestor.
        var physicalSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSourcePath)!, "..", "Legacy.Maliev.AppHost.Topology", "LegacyEdgeReviewPackage.cs"));
        if (File.Exists(physicalSource)) { return physicalSource; }
        throw new FileNotFoundException("Renderer source was not found for the isolated test harness.", physicalSource);
    }
}
