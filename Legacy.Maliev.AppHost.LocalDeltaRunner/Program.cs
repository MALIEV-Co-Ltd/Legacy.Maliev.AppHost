using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using Legacy.Maliev.DataMigration.Console;
using Npgsql;

return await LocalDeltaExecution.RunAsync(Environment.GetEnvironmentVariable);

internal static class LocalDeltaExecution
{
    private const string volumeName = "legacy-maliev-exact23-postgres-data";

    internal static async Task<int> RunAsync(
        Func<string, string?> getEnvironment,
        string? protectedRunRoot = null,
        Func<string, Task>? requireBaseline = null,
        Func<IReadOnlyList<string>, TextWriter, TextWriter, Func<string, string?>, CancellationToken, Task<int>>? runMigration = null,
        TextWriter? output = null,
        TextWriter? error = null)
    {
        requireBaseline ??= RequireBaselineAsync;
        runMigration ??= MigrationConsole.RunAsync;
        string configPath = Required("LEGACY_LOCAL_DELTA_CONFIG", getEnvironment);
        string connectionString = Required("ConnectionStrings__legacy-postgres-main", getEnvironment);
        ValidateOwnerProtected(configPath);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        string targetPath = document.RootElement.GetProperty("delta").GetProperty("targetConnectionFile").GetString()
            ?? throw new InvalidOperationException("The prepared delta config has no target connection file.");
        string runRoot = Path.GetFullPath(protectedRunRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MALIEV", "legacy-delta", "runs"));
        targetPath = Path.GetFullPath(targetPath);
        if (!targetPath.StartsWith(runRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            throw new InvalidOperationException("The delta target connection path is outside the protected run root or already exists.");
        }

        RejectReparseAncestors(targetPath);
        await requireBaseline(connectionString);
        RejectReparseAncestors(targetPath);
        string parent = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Invalid target connection path.");
        CreateOwnerOnlyDirectory(parent);
        bool connectionFileCreated = false;
        try
        {
            await using (FileStream stream = new(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                connectionFileCreated = true;
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(connectionString);
                await writer.FlushAsync();
                stream.Flush(true);
            }
            ProtectFile(targetPath);
            return await runMigration(
                ["apply-delta-local", "--config", configPath], output ?? Console.Out, error ?? Console.Error,
                name => name switch
                {
                    "LEGACY_MIGRATION_CALLER" => "apphost",
                    "LEGACY_DEPLOY_ENABLED" => "false",
                    _ => getEnvironment(name),
                }, CancellationToken.None);
        }
        finally
        {
            if (connectionFileCreated && File.Exists(targetPath)) File.Delete(targetPath);
        }
    }

    static string Required(string name, Func<string, string?> getEnvironment) => getEnvironment(name)?.Trim() is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Required local delta setting '{name}' is missing.");

    static void RejectReparseAncestors(string targetPath)
    {
        for (string? ancestor = Path.GetDirectoryName(targetPath); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            try
            {
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The delta target connection parent path contains a reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    static async Task RequireBaselineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = 1
            FROM legacy_migration_internal.local_baseline_ready
            WHERE volume_name = $1
              AND database_count = 23
              AND baseline_evidence_sha256 ~ '^[0-9a-f]{64}$'
              AND source_cutoff_utc <= reconciled_at_utc
              AND reconciled_at_utc <= CURRENT_TIMESTAMP;
            """, connection);
        _ = command.Parameters.AddWithValue(volumeName);
        if (await command.ExecuteScalarAsync() is not true)
            throw new InvalidOperationException("The persistent PostgreSQL volume has no authenticated exact-23 baseline marker.");
    }

    internal static void ValidateOwnerProtected(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The prepared delta config is missing or is a reparse point.");
        if (!OperatingSystem.IsWindows())
        {
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("The prepared delta config is not owner protected.");
            return;
        }
        if (!IsOwnerOnlyWindows(path))
            throw new InvalidOperationException("The prepared delta config is not owner protected.");
    }

    [SupportedOSPlatform("windows")]
    static bool IsOwnerOnlyWindows(string path)
    {
#pragma warning disable CA1416
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current owner is unavailable.");
        FileSecurity security = new FileInfo(path).GetAccessControl();
        return owner.Equals(security.GetOwner(typeof(SecurityIdentifier))) && security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .All(rule => owner.Equals(rule.IdentityReference));
#pragma warning restore CA1416
    }

    static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
        {
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current owner is unavailable.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
    }

    internal static void ProtectFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        else
        {
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current owner is unavailable.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
    }
}
