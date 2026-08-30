using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Npgsql;

namespace Legacy.Maliev.AppHost.MigrationRunner;

public static class PgRestoreRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunPgRestoreAsync(
        Func<Stream, CancellationToken, Task> writeArchive,
        string databaseName,
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeArchive);
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var host = connection.Host ?? throw new InvalidOperationException("Snapshot connection host is required.");
        var username = connection.Username ?? throw new InvalidOperationException("Snapshot connection username is required.");
        var database = connection.Database ?? throw new InvalidOperationException("Snapshot connection database is required.");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("PG_RESTORE_PATH") ?? "pg_restore",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[]
        {
            "--exit-on-error", "--clean", "--if-exists", "--no-owner", "--no-privileges", "--single-transaction",
            "--no-password", "--host", host,
            "--port", connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--username", username, "--dbname", database,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (!string.IsNullOrWhiteSpace(connection.Password))
        {
            startInfo.Environment["PGPASSWORD"] = connection.Password;
        }

        Process? process = null;
        Task? standardOutputTask = null;
        Task? standardErrorTask = null;
        bool started = false;
        Exception? primaryFailure = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("pg_restore could not be started.");
            }
            started = true;

            standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await writeArchive(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await PgRestoreProcessTermination.AwaitDrainAsync(standardOutputTask, standardErrorTask, CleanupTimeout).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pg_restore failed for database '{databaseName}'.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            primaryFailure = exception is System.ComponentModel.Win32Exception
                ? new InvalidOperationException("The local snapshot requires pg_restore in PATH or PG_RESTORE_PATH.", exception)
                : exception;
        }

        Exception? cleanupFailure = null;
        if (primaryFailure is not null && process is not null && started)
        {
            try
            {
                await PgRestoreProcessTermination.TerminateAndObserveAsync(process, CleanupTimeout).ConfigureAwait(false);
                if (standardOutputTask is not null && standardErrorTask is not null)
                {
                    await PgRestoreProcessTermination.AwaitDrainAsync(standardOutputTask, standardErrorTask, CleanupTimeout)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            {
                cleanupFailure = exception;
            }
        }
        process?.Dispose();

        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException("pg_restore failed and its termination could not be proven.", primaryFailure, cleanupFailure);
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }
}
