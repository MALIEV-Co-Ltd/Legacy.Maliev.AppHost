using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Verified source and listener identity for the Aspire-managed Legacy Web resource.</summary>
public sealed record LegacyWebLaunchIdentity
{
    private static readonly Regex CommitPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    private LegacyWebLaunchIdentity(
        string projectPath,
        string repository,
        string branch,
        string commit,
        int port)
    {
        ProjectPath = projectPath;
        Repository = repository;
        Branch = branch;
        Commit = commit;
        Port = port;
    }

    /// <summary>Gets the exact source project compiled for the Web resource.</summary>
    public string ProjectPath { get; }

    /// <summary>Gets the source repository remote.</summary>
    public string Repository { get; }

    /// <summary>Gets the checked-out source branch.</summary>
    public string Branch { get; }

    /// <summary>Gets the exact 40-character source commit SHA.</summary>
    public string Commit { get; }

    /// <summary>Gets the host port reserved for the Web resource.</summary>
    public int Port { get; }

    /// <summary>Captures and validates source identity prepared by the deterministic start script.</summary>
    /// <returns>The verified source and port identity.</returns>
    public static LegacyWebLaunchIdentity Capture() => Capture(
        Environment.GetEnvironmentVariable,
        static port => IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port));

    internal static LegacyWebLaunchIdentity Capture(
        Func<string, string?> readEnvironment,
        Func<int, bool> isPortInUse)
    {
        var projectPath = Require(readEnvironment, "LEGACY_WEB_PROJECT");
        var normalizedProjectPath = projectPath.Replace('/', '\\');
        if (!normalizedProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
            || normalizedProjectPath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "LEGACY_WEB_PROJECT must identify the Legacy Web source .csproj, never a bin or obj build output.");
        }

        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException($"Legacy Web source project does not exist: {projectPath}");
        }

        var repository = Require(readEnvironment, "LEGACY_WEB_REPOSITORY");
        var branch = Require(readEnvironment, "LEGACY_WEB_BRANCH");
        var commit = Require(readEnvironment, "LEGACY_WEB_COMMIT").ToLowerInvariant();
        if (!CommitPattern.IsMatch(commit))
        {
            throw new InvalidOperationException("LEGACY_WEB_COMMIT must be the exact 40-character Git commit SHA.");
        }

        var portText = Require(readEnvironment, "LEGACY_WEB_PORT");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("LEGACY_WEB_PORT must be an integer from 1 through 65535.");
        }

        if (isPortInUse(port))
        {
            throw new InvalidOperationException(
                $"Legacy Web port {port} is already in use. AppHost will not terminate or reuse the owning process; run the preflight for owner details and coordinate the cutover.");
        }

        return new LegacyWebLaunchIdentity(projectPath, repository, branch, commit, port);
    }

    private static string Require(Func<string, string?> readEnvironment, string name)
    {
        var value = readEnvironment(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{name} is required. Start Aspire through scripts/start-current-web.ps1 so source identity is verified before launch.");
        }

        return value.Trim();
    }
}
