using System.Diagnostics;
using Legacy.Maliev.AppHost.MigrationRunner;

namespace Legacy.Maliev.AppHost.Tests;

[Collection("PgRestoreEnvironment")]
public sealed class PgRestoreProcessTerminationTests
{
    [Fact]
    public async Task TerminateAndObserveAsync_LeavesNoResidualChildProcess()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        start.UseShellExecute = false;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Test process did not start.");
        await PgRestoreProcessTermination.TerminateAndObserveAsync(process, TimeSpan.FromSeconds(5));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task TerminateAndObserveAsync_KillFailureStillPerformsBoundedObservation()
    {
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 30\"");
        start.UseShellExecute = false;
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Test process did not start.");
        var timer = Stopwatch.StartNew();
        try
        {
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() =>
                PgRestoreProcessTermination.TerminateAndObserveAsync(process, TimeSpan.FromMilliseconds(250),
                    _ => throw new InvalidOperationException("controlled kill failure")));
            Assert.Contains(failure.InnerExceptions, exception => exception.Message.Contains("controlled kill failure", StringComparison.Ordinal));
            Assert.Contains(failure.InnerExceptions, exception => exception.Message.Contains("remained alive", StringComparison.Ordinal));
            Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task RunPgRestoreAsync_MissingExecutableFailsWithoutStartingResidualProcess()
    {
        string? previous = Environment.GetEnvironmentVariable("PG_RESTORE_PATH");
        Environment.SetEnvironmentVariable("PG_RESTORE_PATH", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "pg_restore"));
        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PgRestoreRunner.RunPgRestoreAsync((_, _) => Task.CompletedTask, "Synthetic",
                    "Host=127.0.0.1;Port=5432;Database=missing;Username=test;Password=test;SSL Mode=Disable",
                    CancellationToken.None));
            Assert.Contains("requires pg_restore", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PG_RESTORE_PATH", previous);
        }
    }
}

[CollectionDefinition("PgRestoreEnvironment", DisableParallelization = true)]
public sealed class PgRestoreEnvironmentCollection;
