using System.Reflection;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyWebLaunchIdentityTests
{
    private static readonly Assembly TopologyAssembly = typeof(Topology.LegacyTopology).Assembly;

    [Fact]
    public void Capture_ValidSourceIdentity_ReturnsExactValues()
    {
        var identity = Capture(
            new Dictionary<string, string?>
            {
                ["LEGACY_WEB_PROJECT"] = SourceProjectPath,
                ["LEGACY_WEB_REPOSITORY"] = "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web.git",
                ["LEGACY_WEB_BRANCH"] = "main",
                ["LEGACY_WEB_COMMIT"] = "6e00796d263c45be73080fa292929a99dbb9af1d",
                ["LEGACY_WEB_PORT"] = "5188"
            },
            static _ => false);

        Assert.Equal("https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web.git", GetProperty<string>(identity, "Repository"));
        Assert.Equal("main", GetProperty<string>(identity, "Branch"));
        Assert.Equal("6e00796d263c45be73080fa292929a99dbb9af1d", GetProperty<string>(identity, "Commit"));
        Assert.Equal(5188, GetProperty<int>(identity, "Port"));
    }

    [Fact]
    public void Capture_OccupiedPort_FailsWithoutOwningProcessTermination()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Capture(ValidEnvironment(), static port => port == 5088));

        var cause = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("5088", cause.Message, StringComparison.Ordinal);
        Assert.Contains("already in use", cause.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not terminate", cause.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"B:\maliev\Legacy.Maliev.Web\.worktrees\old\Legacy.Maliev.Web\bin\Release\net10.0\Legacy.Maliev.Web.dll")]
    [InlineData(@"B:\maliev\Legacy.Maliev.Web\Legacy.Maliev.Web\obj\Legacy.Maliev.Web.csproj")]
    public void Capture_BuildOutputPath_IsRejected(string projectPath)
    {
        var values = ValidEnvironment();
        values["LEGACY_WEB_PROJECT"] = projectPath;

        var error = Assert.Throws<TargetInvocationException>(() => Capture(values, static _ => false));

        var cause = Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("source .csproj", cause.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static object Capture(IReadOnlyDictionary<string, string?> values, Func<int, bool> isPortInUse)
    {
        var type = TopologyAssembly.GetType("Legacy.Maliev.AppHost.Topology.LegacyWebLaunchIdentity");
        Assert.NotNull(type);
        var method = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == "Capture" && candidate.GetParameters().Length == 2);
        Assert.NotNull(method);
        Func<string, string?> readEnvironment = name => values.GetValueOrDefault(name);
        var identity = method.Invoke(null, [readEnvironment, isPortInUse]);
        Assert.IsType(type, identity);
        return identity;
    }

    private static T GetProperty<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(instance));
    }

    private static Dictionary<string, string?> ValidEnvironment() => new()
    {
        ["LEGACY_WEB_PROJECT"] = SourceProjectPath,
        ["LEGACY_WEB_REPOSITORY"] = "https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web.git",
        ["LEGACY_WEB_BRANCH"] = "main",
        ["LEGACY_WEB_COMMIT"] = "6e00796d263c45be73080fa292929a99dbb9af1d",
        ["LEGACY_WEB_PORT"] = "5088"
    };

    private static string SourceProjectPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var projectPath = Path.Combine(
                    directory.FullName,
                    "Legacy.Maliev.AppHost.Topology",
                    "Legacy.Maliev.AppHost.Topology.csproj");
                if (File.Exists(projectPath))
                {
                    return projectPath;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate a repository-local source project for the test.");
        }
    }
}
