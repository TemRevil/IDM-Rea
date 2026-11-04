namespace IDMRea.Engine.Windows;

public static class RecycleBin
{
    /// <summary>Moves a file to the Recycle Bin without asking or showing progress. True only if the file is gone.</summary>
    public static bool Send(string path)
    {
        if (!File.Exists(path)) return false;
        var op = new Native.SHFILEOPSTRUCTW
        {
            wFunc = Native.FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI,
        };
        return Native.SHFileOperationW(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }
}
