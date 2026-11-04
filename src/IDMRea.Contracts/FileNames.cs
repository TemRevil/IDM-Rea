namespace IDMRea.Contracts;

public static class FileNames
{
    private static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>A name Windows accepts: no reserved characters or device names, no trailing dots, at most 180 characters.</summary>
    public static string SafeName(string name)
    {
        name = new string(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (Reserved.Contains(stem)) name = "_" + name;
        return name.Length > 180 ? name[..180] : name;
    }

    /// <summary>The first of "name.ext", "name (1).ext", "name (2).ext"... in <paramref name="folder"/> that <paramref name="taken"/> rejects.</summary>
    public static string UniquePath(string folder, string name, Func<string, bool> taken)
    {
        var path = Path.Combine(folder, name); var n = 1;
        while (taken(path)) path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n++}){Path.GetExtension(name)}");
        return path;
    }
}
