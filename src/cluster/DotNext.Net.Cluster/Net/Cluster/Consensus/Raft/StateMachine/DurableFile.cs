using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

internal static partial class DurableFile
{
    internal static void Flush(SafeFileHandle file)
    {
        RandomAccess.FlushToDisk(file);
        if (OperatingSystem.IsMacOS() && FullSync(file, 51) != 0)
            throw NativeIOException("Cannot durably flush the WAL file.");
    }

    internal static void Publish(string temporaryPath, string destinationPath)
    {
        temporaryPath = Path.GetFullPath(temporaryPath);
        destinationPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !string.Equals(Path.GetDirectoryName(temporaryPath), directory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Durable WAL publication requires files in the same directory.", nameof(temporaryPath));

        using (var file = File.OpenHandle(temporaryPath, access: FileAccess.Write))
            Flush(file);

        if (OperatingSystem.IsWindows())
        {
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
            if (!MoveFile(temporaryPath, destinationPath, 0x1U | 0x8U))
                throw NativeIOException("Cannot publish the WAL file.");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        else
        {
            throw new PlatformNotSupportedException("Durable WAL publication is not supported on this operating system.");
        }

        FlushPublication(new FileInfo(destinationPath));
    }

    internal static void FlushPublication(FileInfo file)
    {
        FlushDirectory(file.Directory!);
        if (OperatingSystem.IsMacOS())
        {
            using var handle = File.OpenHandle(file.FullName, access: FileAccess.Write,
                share: FileShare.ReadWrite | FileShare.Delete);
            Flush(handle);
        }
    }

    internal static void FlushDirectory(DirectoryInfo directory)
    {
        using var handle = OperatingSystem.IsWindows()
            ? OpenWindowsDirectory(directory.FullName, 0x40000000U, 0x7U, 0, 3U, 0x02000000U, 0)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()
                ? new SafeFileHandle(OpenUnixDirectory(directory.FullName, 0), ownsHandle: true)
                : throw new PlatformNotSupportedException("Durable WAL directory publication is not supported on this operating system.");
        if (handle.IsInvalid)
            throw NativeIOException("Cannot open the WAL directory for a durability barrier.");
        RandomAccess.FlushToDisk(handle);
    }

    private static IOException NativeIOException(string message)
        => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFile(string existingFileName, string newFileName, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle OpenWindowsDirectory(string path, uint access, uint share, nint securityAttributes,
        uint creationDisposition, uint flags, nint template);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenUnixDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FullSync(SafeFileHandle file, int command);
}
