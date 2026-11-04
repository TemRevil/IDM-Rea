using System.Runtime.InteropServices;
using IDMRea.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
namespace IDMRea.App;

public partial class App : Application
{
    private const string InstanceKey = "IDMRea.Main";
    private MainWindow? window;

    /// <summary>One app window at a time: a second launch hands its arguments to the first and exits.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            AllowSetForegroundWindow((uint)main.ProcessId);
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => main.RedirectActivationToAsync(activation).AsTask()).Wait(TimeSpan.FromSeconds(10));
            return 0;
        }
        Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
        return 0;
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Protocol.DataDirectory);
            File.AppendAllText(Path.Combine(Protocol.DataDirectory, "app-startup.log"), $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    public App()
    {
        UnhandledException += (_, e) => Log($"Unhandled: {e.Exception}");
        InitializeComponent();
    }

    private static LaunchArguments Arguments(AppActivationArguments activation) =>
        LaunchArguments.Parse(activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        var queue = DispatcherQueue.GetForCurrentThread();
        AppInstance.GetCurrent().Activated += (_, e) => queue.TryEnqueue(() => window.Reactivate(Arguments(e)));
        window.Activate();
        window.Handle(Arguments(AppInstance.GetCurrent().GetActivatedEventArgs()));
    }
}
