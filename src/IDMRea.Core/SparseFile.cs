using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace IDMRea.Core;

/// <summary>
/// On Windows a write past a file's valid data first zero-fills everything before it, so a part starting far into a
/// pre-sized file waits for the whole gap (about 10 s per 448 MB on a USB disk). Marking the part file sparse skips
/// that; the mark comes off once every byte is written. Elsewhere this does nothing: those file systems leave holes anyway.
/// Each call opens its own synchronous handle, since the call below has no overlapped form on an asynchronous one.
/// </summary>
internal static class SparseFile
{
    private const uint SetSparse = 0x000900C4; // FSCTL_SET_SPARSE

    public static void Mark(string path) => Set(path, true);

    /// <summary>Call only when the file has no holes left (every byte written, or emptied), or clearing fills them.</summary>
    public static void Unmark(string path) => Set(path, false);

    /// <summary>Best effort: a file system without sparse files (FAT, exFAT) just keeps the slower path.</summary>
    private static void Set(string path, bool sparse)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var handle = File.OpenHandle(path, sparse ? FileMode.OpenOrCreate : FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var value = sparse ? (byte)1 : (byte)0; // FILE_SET_SPARSE_BUFFER.SetSparse
            DeviceIoControl(handle, SetSparse, ref value, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref byte input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
