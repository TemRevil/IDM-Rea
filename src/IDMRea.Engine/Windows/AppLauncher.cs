using System.Diagnostics;
namespace IDMRea.Engine.Windows;

/// <summary>Starts the app window, which sits one folder above the engine. The app brings an existing window forward itself.</summary>
public static class AppLauncher
{
    public static string AppFolder => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));

    public static void Open(string? args)
    {
        var app = Path.Combine(AppFolder, "IDMRea.App.exe");
        if (!File.Exists(app)) return;
        Process.Start(new ProcessStartInfo(app, args ?? "") { UseShellExecute = false, WorkingDirectory = AppFolder })?.Dispose();
    }
}
