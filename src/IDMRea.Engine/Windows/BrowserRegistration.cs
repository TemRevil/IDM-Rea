using System.Text.Json;
using Microsoft.Win32;
namespace IDMRea.Engine.Windows;

/// <summary>
/// Lets Chrome, Edge and other Chromium browsers start the native host for the IDM-Rea extension: a host manifest in
/// the data folder, one per-user registry key per browser pointing at it, and <c>browser-target.txt</c> in the default
/// IDM-Rea folder naming the data folder the host should use (the browser starts it without the app's environment).
/// </summary>
public sealed class BrowserRegistration(string keyPrefix = "Software")
{
    public const string HostName = "com.idmrea.host";
    /// <summary>Fixed by the <c>key</c> in the extension's manifest.</summary>
    public const string ExtensionId = "ioenfhcbkjnjlpkmjngfhohmimmjgllg";
    public const string TargetFileName = "browser-target.txt";
    private static readonly string[] Browsers = [@"Google\Chrome", @"Microsoft\Edge", "Chromium"];

    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMRea");

    public void Register(string hostExe, string dataFolder, string home)
    {
        dataFolder = Path.GetFullPath(dataFolder);
        var folder = Path.Combine(dataFolder, "browser");
        Directory.CreateDirectory(folder);
        var manifest = Path.Combine(folder, HostName + ".json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
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
            key.SetValue("", manifest, RegistryValueKind.String);
        }
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, TargetFileName), dataFolder);
    }

    /// <summary>Rewrites the host path (the app may have moved) only when the registration belongs to this data folder.</summary>
    public bool RefreshIfOwned(string hostExe, string dataFolder, string home)
    {
        var target = Path.Combine(home, TargetFileName);
        if (!File.Exists(target)) return false;
        if (!string.Equals(Path.GetFullPath(File.ReadAllText(target).Trim()), Path.GetFullPath(dataFolder), StringComparison.OrdinalIgnoreCase)) return false;
        Register(hostExe, dataFolder, home);
        return true;
    }
}
