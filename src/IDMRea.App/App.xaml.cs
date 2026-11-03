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
        UnhandledException += (_, e) =>
        {
            Log($"Unhandled message: {e.Message}; HRESULT={e.Exception.HResult:X8}");
            Log(e.Exception.ToString());
            foreach (System.Collections.DictionaryEntry item in e.Exception.Data) Log($"Exception data {item.Key}: {item.Value}");
        };
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log($"XAML resource: {e.Message}");
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("Creating MainWindow");
        window = new MainWindow(); window.Activate();
        Log($"Window activated; HWND={WinRT.Interop.WindowNative.GetWindowHandle(window)}");
    }
}
