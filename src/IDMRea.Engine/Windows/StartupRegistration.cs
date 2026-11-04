using Microsoft.Win32;
namespace IDMRea.Engine.Windows;

/// <summary>
/// The per-user Run entry that starts the engine in the tray when Windows starts. It names the data folder when it
/// isn't the default, and an engine removes the entry only when it is its own, so an engine on another folder (a test
/// run, a second copy) never turns off the start-up of the one that set it.
/// </summary>
public sealed class StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "IDM-Rea")
{
    public static string Command(string exePath, string? dataFolder) =>
        dataFolder is null ? $"\"{exePath}\" --tray" : $"\"{exePath}\" --tray --data-directory \"{dataFolder}\"";

    /// <param name="dataFolder">The engine's data folder, or null for the default one.</param>
    public void Set(bool on, string exePath, string? dataFolder = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        var mine = Command(exePath, dataFolder);
        if (on) key.SetValue(valueName, mine, RegistryValueKind.String);
        else if (string.Equals(key.GetValue(valueName) as string, mine, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
