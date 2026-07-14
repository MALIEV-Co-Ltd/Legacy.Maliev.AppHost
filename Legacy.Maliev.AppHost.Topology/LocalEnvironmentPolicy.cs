namespace Legacy.Maliev.AppHost.Topology;

public static class LocalEnvironmentPolicy
{
    private static readonly HashSet<string> PreservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "APPDATA",
        "ASPNETCORE_ENVIRONMENT",
        "COMPUTERNAME",
        "COMSPEC",
        "DOTNET_CLI_HOME",
        "DOTNET_ENVIRONMENT",
        "DOTNET_LAUNCH_PROFILE",
        "GITHUB_ACTIONS",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "PROGRAMDATA",
        "SYSTEMDRIVE",
        "SYSTEMROOT",
        "TEMP",
        "TMP",
        "USERPROFILE",
        "WINDIR",
    };

    private static readonly string[] PreservedPrefixes =
    [
        "ASPIRE_",
        "ASPNETCORE_",
        "DOTNET_",
        "Parameters__legacy-",
        "PROGRAMFILES",
        "COMMONPROGRAMFILES",
    ];

    public static bool ShouldPreserve(string variableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);

        return PreservedNames.Contains(variableName)
            || PreservedPrefixes.Any(prefix => variableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static void SanitizeCurrentProcess()
    {
        var variableNames = Environment.GetEnvironmentVariables()
            .Keys
            .Cast<object>()
            .Select(static key => key.ToString())
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .ToArray();

        foreach (var variableName in variableNames)
        {
            if (!ShouldPreserve(variableName))
            {
                Environment.SetEnvironmentVariable(variableName, null);
            }
        }
    }
}
