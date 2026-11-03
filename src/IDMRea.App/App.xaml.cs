using Microsoft.UI.Xaml;
using IDMRea.Contracts;
namespace IDMRea.App;
public partial class App : Application
{
    private Window? window;
    private static void Log(string message)
    {
        Directory.CreateDirectory(Protocol.DataDirectory);
        File.AppendAllText(Path.Combine(Protocol.DataDirectory, "app-startup.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
    }
    public App()
    {
        Log($"Application constructed; interactive={Environment.UserInteractive}; session={System.Diagnostics.Process.GetCurrentProcess().SessionId}");
        UnhandledException += (_, e) => Log(e.Exception.ToString());
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("Creating MainWindow");
        window = new MainWindow(); window.Activate();
        Log($"Window activated; HWND={WinRT.Interop.WindowNative.GetWindowHandle(window)}");
    }
}
