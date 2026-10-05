using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Checks reviewed renderer artifact and source correspondence, not whole-build provenance.</summary>
public static class LegacyRendererSourceProvenance
{
    private static readonly Guid Sha256DocumentAlgorithm = new("8829d00f-11b8-4213-878b-770e8597ac16");

    /// <summary>Checks a reviewed DLL's linked portable PDB against the current renderer source.</summary>
    public static void Validate(string assemblyPath, string pdbPath, string rendererSourcePath, string expectedAssemblySha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAssemblySha256);
        if (expectedAssemblySha256.Length != 64 || !expectedAssemblySha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("An explicit reviewed assembly SHA256 is required.", nameof(expectedAssemblySha256));
        }
        using var dllStream = File.OpenRead(assemblyPath);
        var actualHash = SHA256.HashData(dllStream);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedAssemblySha256)))
        {
            throw new InvalidDataException("Selected renderer assembly differs from the reviewed artifact.");
        }
        dllStream.Position = 0;
        using var pe = new PEReader(dllStream);
        var entries = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        if (entries.Length != 1) { throw new InvalidDataException("One linked CodeView record is required."); }
        var codeView = pe.ReadCodeViewDebugDirectoryData(entries[0]);
        using var pdbStream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader = provider.GetMetadataReader();
        var id = reader.DebugMetadataHeader?.Id.ToArray() ?? throw new InvalidDataException("Portable PDB identity is missing.");
        if (id.Length != 20 || new Guid(id.AsSpan(0, 16)) != codeView.Guid || codeView.Age != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(16, 4)) != entries[0].Stamp)
        {
            throw new InvalidDataException("Portable PDB does not belong to the reviewed DLL.");
        }
        var documents = reader.Documents.Select(handle => reader.GetDocument(handle))
            .Where(document => Path.GetFileName(reader.GetString(document.Name).Replace('\\', '/')) == "LegacyEdgeReviewPackage.cs")
            .ToArray();
        if (documents.Length != 1 || documents[0].HashAlgorithm.IsNil || documents[0].Hash.IsNil
            || reader.GetGuid(documents[0].HashAlgorithm) != Sha256DocumentAlgorithm)
        {
            throw new InvalidDataException("A unique SHA256 renderer source document is required.");
        }
        var compiledChecksum = reader.GetBlobBytes(documents[0].Hash);
        using var currentSource = File.OpenRead(rendererSourcePath);
        if (!CryptographicOperations.FixedTimeEquals(compiledChecksum, SHA256.HashData(currentSource)))
        {
            throw new InvalidDataException("Renderer source differs from the reviewed compiled document.");
        }
    }
}
