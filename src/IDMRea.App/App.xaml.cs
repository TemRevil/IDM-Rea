using System.Runtime.InteropServices;
using IDMRea.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
namespace IDMRea.App;

public partial class App : Application
{
    private MainWindow? window;

    /// <summary>One app window per data folder: a second launch hands its arguments to the first and exits.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var main = AppInstance.FindOrRegisterForKey(Protocol.InstanceKey);
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

    public App()
    {
        UnhandledException += (_, e) => Log.Crash("App", $"IDM-Rea closed on an error it didn't expect ({Log.Place}): {e.Message}", e.Exception);
        InitializeComponent();
    }

    private static LaunchArguments Arguments(AppActivationArguments activation) =>
        LaunchArguments.Parse(activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ? launch.Arguments : "");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        Log.Start(queue);
        window = new MainWindow();
        AppInstance.GetCurrent().Activated += (_, e) => queue.TryEnqueue(() => window.Reactivate(Arguments(e)));
        window.Activate();
        Log.Where = "downloads"; // until now the window was still being built: "while starting"
        window.Handle(Arguments(AppInstance.GetCurrent().GetActivatedEventArgs()));
    }
}
