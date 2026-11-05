using System.Text.Json;
using IDMRea.Contracts;
using Microsoft.Win32;
namespace IDMRea.Engine.Windows;

/// <summary>
/// Lets Chrome, Edge and other Chromium browsers start the native host for the IDM-Rea extension: a host manifest in
/// the data folder, one per-user registry key per browser pointing at it, and <c>browser-target.txt</c> in the default
/// IDM-Rea folder naming the data folder the host should use (the browser starts it without the app's environment).
/// It also copies the extension into the data folder, so the folder the browser loads it from outlives the app's own.
/// </summary>
public sealed class BrowserRegistration(string keyPrefix = "Software")
{
    public const string HostName = "com.idmrea.host";
    /// <summary>Fixed by the <c>key</c> in the extension's manifest.</summary>
    public const string ExtensionId = "ioenfhcbkjnjlpkmjngfhohmimmjgllg";
    public const string TargetFileName = "browser-target.txt";
    private static readonly string[] Browsers = [@"Google\Chrome", @"Microsoft\Edge", "Chromium"];

    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMRea");

    public void Register(string hostExe, string dataFolder, string home, string extensionSource)
    {
        if (!File.Exists(Path.Combine(extensionSource, "manifest.json")))
            throw new InvalidOperationException("The extension folder isn't next to the app. Install IDM-Rea again to get it.");
        dataFolder = Path.GetFullPath(dataFolder);
        var folder = Path.Combine(dataFolder, "browser");
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, HostName + ".json");
        WriteIfChanged(manifest, JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "IDM-Rea download engine",
            path = hostExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var browser in Browsers)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{keyPrefix}\{browser}\NativeMessagingHosts\{HostName}", writable: true);
            if (key.GetValue("") as string != manifest) key.SetValue("", manifest, RegistryValueKind.String);
        }
        Directory.CreateDirectory(home);
        WriteIfChanged(Path.Combine(home, TargetFileName), dataFolder);
        Mirror(extensionSource, Protocol.ExtensionFolderFor(dataFolder));
    }

    // The browser reads these files whenever the extension talks to IDM-Rea and while it runs, and every engine start
    // calls Register: files are written only when their content changes, and never left half-written.
    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Makes <paramref name="target"/> an exact copy of <paramref name="source"/>, touching only the files that
    /// differ. The folder itself stays: a browser running the extension from it would otherwise see it vanish.</summary>
    private static void Mirror(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            wanted.Add(relative);
            var copy = Path.Combine(target, relative);
            if (File.Exists(copy) && SameBytes(file, copy)) continue;
            File.Copy(file, copy + ".tmp", overwrite: true);
            File.Move(copy + ".tmp", copy, overwrite: true);
        }
        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
            if (!wanted.Contains(Path.GetRelativePath(target, file))) File.Delete(file);
        foreach (var dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            if (!Directory.Exists(Path.Combine(source, Path.GetRelativePath(target, dir))) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    private static bool SameBytes(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    /// <summary>Rewrites the host path (the app may have moved) only when the registration belongs to this data folder.</summary>
    public bool RefreshIfOwned(string hostExe, string dataFolder, string home, string extensionSource)
    {
        var target = Path.Combine(home, TargetFileName);
        if (!File.Exists(target)) return false;
        if (!string.Equals(Path.GetFullPath(File.ReadAllText(target).Trim()), Path.GetFullPath(dataFolder), StringComparison.OrdinalIgnoreCase)) return false;
        Register(hostExe, dataFolder, home, extensionSource);
        return true;
    }
}
