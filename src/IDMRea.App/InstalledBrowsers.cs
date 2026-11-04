using Microsoft.Win32;
namespace IDMRea.App;

/// <summary>A browser on this PC that can load the IDM-Rea extension, with the address of its extensions page.</summary>
public sealed record InstalledBrowser(string Name, string ExePath, string ExtensionsPage);

/// <summary>Finds Chrome and Edge from their App Paths entries, the same place Windows looks when it starts them by name.</summary>
public static class InstalledBrowsers
{
    public const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    private static readonly (string Name, string Exe, string Page)[] Known =
        [("Chrome", "chrome.exe", "chrome://extensions/"), ("Edge", "msedge.exe", "edge://extensions/")];

    /// <summary>The browsers installed for this user or for everyone, each once.</summary>
    public static IReadOnlyList<InstalledBrowser> All() =>
        Find(Registry.CurrentUser).Concat(Find(Registry.LocalMachine)).DistinctBy(b => b.Name).ToList();

    /// <summary>Known browsers whose App Paths entry under <paramref name="root"/> names a file that exists.</summary>
    public static IReadOnlyList<InstalledBrowser> Find(RegistryKey root, string appPaths = AppPaths)
    {
        var found = new List<InstalledBrowser>();
        foreach (var (name, exe, page) in Known)
        {
            try
            {
                using var key = root.OpenSubKey($@"{appPaths}\{exe}");
                var path = (key?.GetValue("") as string)?.Trim().Trim('"');
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) found.Add(new(name, path, page));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return found;
    }
}
