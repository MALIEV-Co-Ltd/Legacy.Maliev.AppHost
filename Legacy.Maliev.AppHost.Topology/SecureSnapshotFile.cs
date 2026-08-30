using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Legacy.Maliev.AppHost.Topology;

internal static class SecureSnapshotFile
{
    public static FileStream OpenRead(string path, bool exclusive = false)
    {
        string full = Path.GetFullPath(path);
        EnsureNoLinkAncestors(full);
        var file = new FileInfo(full); file.Refresh();
        if (!file.Exists || file.LinkTarget is not null || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Snapshot ciphertext must be a regular non-link file.");
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, exclusive ? FileShare.None : FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (!HandleResolvesTo(stream.SafeFileHandle, full) || !OpenedHandleIsOwnerOnlyRegular(stream.SafeFileHandle, full))
                throw new InvalidOperationException("Snapshot ciphertext opened-handle identity or permissions are unsafe.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static bool OpenedHandleIsOwnerOnlyRegular(SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsWindows()) return IsOwnerOnlyWindows(path);
        if (!OperatingSystem.IsLinux() || Statx(checked((int)handle.DangerousGetHandle()), string.Empty, AtEmptyPath,
            StatxBasicStats, out LinuxStatx stat) != 0) return false;
        const ushort fileTypeMask = 0xF000, regularFile = 0x8000, groupOtherMask = 0x003F, ownerRead = 0x0100;
        return stat.Uid == GetEffectiveUserIdNative() && (stat.Mode & fileTypeMask) == regularFile &&
            (stat.Mode & groupOtherMask) == 0 && (stat.Mode & ownerRead) != 0;
    }

    private static bool HandleResolvesTo(SafeFileHandle handle, string expected)
    {
        string? observed = OperatingSystem.IsWindows() ? FinalWindowsPath(handle) :
            OperatingSystem.IsLinux() ? File.ResolveLinkTarget($"/proc/self/fd/{handle.DangerousGetHandle()}", true)?.FullName : null;
        return observed is not null && string.Equals(Path.GetFullPath(observed), Path.GetFullPath(expected),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void EnsureNoLinkAncestors(string path)
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(path)!); current is not null; current = current.Parent)
        {
            current.Refresh();
            if (current.Exists && (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidOperationException("Snapshot path contains a symbolic link or reparse point.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyWindows(string path)
    {
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
        FileSecurity security = new FileInfo(path).GetAccessControl();
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))) return false;
        return security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .All(rule => rule.AccessControlType == AccessControlType.Deny || owner.Equals(rule.IdentityReference));
    }

    [SupportedOSPlatform("windows")]
    private static string? FinalWindowsPath(SafeFileHandle handle)
    {
        var buffer = new char[4096]; uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        string value = new(buffer, 0, checked((int)length));
        const string unc = @"\\?\UNC\", device = @"\\?\";
        return value.StartsWith(unc, StringComparison.OrdinalIgnoreCase) ? @"\\" + value[unc.Length..] :
            value.StartsWith(device, StringComparison.Ordinal) ? value[device.Length..] : value;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, [Out] char[] path, uint capacity, uint flags);
#pragma warning restore SYSLIB1054

    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx { [FieldOffset(20)] public uint Uid; [FieldOffset(28)] public ushort Mode; }

#pragma warning disable SYSLIB1054, CA2101
    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    private static extern uint GetEffectiveUserIdNative();
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx stat);
#pragma warning restore SYSLIB1054, CA2101
}
