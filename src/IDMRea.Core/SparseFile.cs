using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace IDMRea.Core;

/// <summary>
/// On Windows a write past a file's valid data first zero-fills everything before it, so a part starting far into a
/// pre-sized file waits for the whole gap (about 10 s per 448 MB on a USB disk). Marking the part file sparse skips
/// that; the mark comes off once every byte is written. Elsewhere this does nothing: those file systems leave holes anyway.
/// </summary>
internal static class SparseFile
{
    private const uint SetSparse = 0x000900C4; // FSCTL_SET_SPARSE

    public static void Mark(SafeFileHandle handle) => Set(handle, true);
    public static void Unmark(SafeFileHandle handle) => Set(handle, false);

    /// <summary>Best effort: a file system without sparse files (FAT, exFAT) just keeps the slower path.</summary>
    private static void Set(SafeFileHandle handle, bool sparse)
    {
        if (!OperatingSystem.IsWindows()) return;
        var value = sparse ? (byte)1 : (byte)0; // FILE_SET_SPARSE_BUFFER.SetSparse
        DeviceIoControl(handle, SetSparse, ref value, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref byte input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
