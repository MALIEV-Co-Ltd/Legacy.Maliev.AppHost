using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Legacy.Maliev.AppHost.MigrationRunner;

public static class PgRestoreProcessTermination
{
    public static async Task TerminateAndObserveAsync(Process process, TimeSpan timeout)
        => await TerminateAndObserveAsync(process, timeout, target => target.Kill(entireProcessTree: true)).ConfigureAwait(false);

    internal static async Task TerminateAndObserveAsync(Process process, TimeSpan timeout, Action<Process> kill)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(kill);
        ProcessIdentity[] descendants = CaptureDescendants(process.Id);
        Exception? killFailure = null;
        if (!process.HasExited)
        {
            try { kill(process); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
            { killFailure = exception; }
        }
        using var deadline = new CancellationTokenSource(timeout);
        Exception? observationFailure = null;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            while (descendants.Any(IsStillRunning)) await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        { observationFailure = new InvalidOperationException("pg_restore or a descendant remained alive after termination.", exception); }
        if (!process.HasExited || descendants.Any(IsStillRunning))
            observationFailure ??= new InvalidOperationException("pg_restore or a descendant remained alive after termination.");
        if (killFailure is not null && observationFailure is not null)
            throw new AggregateException("pg_restore kill failed and bounded process observation also failed.", killFailure, observationFailure);
        if (killFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(killFailure).Throw();
        if (observationFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
    }

    public static Task AwaitDrainAsync(Task standardOutput, Task standardError, TimeSpan timeout)
        => Task.WhenAll(standardOutput, standardError).WaitAsync(timeout);

    private static bool IsStillRunning(ProcessIdentity identity)
        => IsStillRunning(identity, Process.GetProcessById, process => process.StartTime.ToUniversalTime().Ticks);

    internal static bool IsStillRunning(
        ProcessIdentity identity,
        Func<int, Process> findProcess,
        Func<Process, long> getStartTimeUtcTicks)
    {
        try
        {
            using Process observed = findProcess(identity.Id);
            return !observed.HasExited && getStartTimeUtcTicks(observed) == identity.StartTimeUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private static ProcessIdentity[] CaptureDescendants(int root)
    {
        Dictionary<int, int> parents = OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();
        var pending = new Queue<int>(); pending.Enqueue(root); var results = new List<ProcessIdentity>();
        while (pending.TryDequeue(out int parent))
        {
            foreach (int child in parents.Where(pair => pair.Value == parent).Select(pair => pair.Key))
            {
                pending.Enqueue(child);
                try { using Process process = Process.GetProcessById(child); results.Add(new(child, process.StartTime.ToUniversalTime().Ticks)); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
            }
        }
        return [.. results];
    }

    private static Dictionary<int, int> LinuxParents()
    {
        var result = new Dictionary<int, int>(); if (!OperatingSystem.IsLinux()) return result;
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int id)) continue;
            try
            {
                string? line = File.ReadLines(Path.Combine(directory, "status"))
                    .FirstOrDefault(value => value.StartsWith("PPid:", StringComparison.Ordinal));
                if (line is not null && int.TryParse(line.AsSpan(5).Trim(), out int parent)) result[id] = parent;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static Dictionary<int, int> WindowsParents()
    {
        var result = new Dictionary<int, int>(); if (!OperatingSystem.IsWindows()) return result;
        nint snapshot = CreateToolhelp32Snapshot(2, 0); if (snapshot == new nint(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do { result[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId); } while (Process32Next(snapshot, ref entry));
        }
        finally { _ = CloseHandle(snapshot); }
        return result;
    }

    internal sealed record ProcessIdentity(int Id, long StartTimeUtcTicks);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId; public nint DefaultHeapId; public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054
}
