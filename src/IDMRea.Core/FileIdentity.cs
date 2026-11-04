using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace IDMRea.Core;

/// <summary>
/// Which file this is, cheaply: on Windows the volume serial number and file index, which stay with a file when it is
/// renamed or moved on the same drive. Used after a crash to tell a finished download from an unrelated file of the
/// same size. Null where the system can't say (other platforms, some network drives).
/// </summary>
public static class FileIdentity
{
    public static string? Of(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(handle, out var info) || (info.IndexHigh == 0 && info.IndexLow == 0)) return null;
            return $"{info.VolumeSerial:X8}-{info.IndexHigh:X8}{info.IndexLow:X8}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation // BY_HANDLE_FILE_INFORMATION; each FILETIME is two DWORDs
    {
        public uint Attributes, Created1, Created2, Accessed1, Accessed2, Written1, Written2, VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
