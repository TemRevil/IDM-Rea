using Microsoft.Win32;
namespace IDMRea.Engine.Windows;

/// <summary>The per-user Run entry that starts the engine in the tray when Windows starts.</summary>
public sealed class StartupRegistration(string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", string valueName = "IDM-Rea")
{
    public void Set(bool on, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
        if (on) key.SetValue(valueName, $"\"{exePath}\" --tray", RegistryValueKind.String);
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
