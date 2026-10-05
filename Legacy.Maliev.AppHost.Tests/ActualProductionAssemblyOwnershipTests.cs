using System.Reflection;
using Aspire.Hosting;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class ActualProductionAssemblyOwnershipTests
{
    [Theory]
    [InlineData("Legacy.Maliev.AppHost", true)]
    [InlineData("Legacy.Maliev.AppHost.MigrationRunner", true)]
    [InlineData("Legacy.Maliev.AppHost.LocalDeltaRunner", true)]
    [InlineData("Legacy.Maliev.AppHost.Topology", false)]
    public void ActualProductionArtifact_IsPresentWithItsPdbAndExpectedEntryPoint(string name, bool executable)
    {
        var assembly = Assembly.Load(new AssemblyName(name));
        Assert.Equal(name, assembly.GetName().Name);
        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), Path.GetDirectoryName(assembly.Location) + Path.DirectorySeparatorChar);
        Assert.True(File.Exists(Path.ChangeExtension(assembly.Location, ".pdb")), "The production artifact requires its own matching PDB for raw ownership validation.");
        Assert.Equal(executable, assembly.EntryPoint is not null);
        Assert.NotEqual(typeof(ActualProductionAssemblyOwnershipTests).Assembly, assembly);
        // Loading metadata does not invoke an executable entry point or start services.
    }

    [Theory]
    [InlineData(typeof(Projects.Legacy_Maliev_AuthService_Api), "Legacy.Maliev.AuthService/Legacy.Maliev.AuthService.Api/Legacy.Maliev.AuthService.Api.csproj")]
    [InlineData(typeof(Projects.Legacy_Maliev_DocumentService_Api), "Legacy.Maliev.DocumentService/Legacy.Maliev.DocumentService.Api/Legacy.Maliev.DocumentService.Api.csproj")]
    [InlineData(typeof(Projects.Legacy_Maliev_NotificationService_Api), "Legacy.Maliev.NotificationService/Legacy.Maliev.NotificationService.Api/Legacy.Maliev.NotificationService.Api.csproj")]
    [InlineData(typeof(Projects.Legacy_Maliev_Intranet_Bff), "Legacy.Maliev.Intranet/Legacy.Maliev.Intranet.Bff/Legacy.Maliev.Intranet.Bff.csproj")]
    [InlineData(typeof(Projects.Legacy_Maliev_Web), "Legacy.Maliev.Web/Legacy.Maliev.Web/Legacy.Maliev.Web.csproj")]
    public void GeneratedProjectBinding_ResolvesTheRetainedProducerAndSuppressesDuplicateBuild(Type metadataType, string retainedPath)
    {
        var metadata = Assert.IsAssignableFrom<IProjectMetadata>(Activator.CreateInstance(metadataType));
        var actualPath = Path.GetFullPath(metadata.ProjectPath).Replace('\\', '/');
        Assert.True(File.Exists(actualPath), "Generated metadata must bind an existing retained source project.");
        Assert.EndsWith("/" + retainedPath, actualPath, StringComparison.Ordinal);
        var suppressBuild = metadataType.GetProperty("SuppressBuild") ?? throw new InvalidOperationException("Generated metadata must declare its build ownership.");
        Assert.True(Assert.IsType<bool>(suppressBuild.GetValue(metadata)));
        Assert.Equal("Legacy.Maliev.AppHost", metadataType.Assembly.GetName().Name);
    }
}
