using IDMRea.Contracts;
using IDMRea.Core;
using IDMRea.Engine;

// The start-up entry names the data folder when it isn't the default (see StartupRegistration).
if (Array.IndexOf(args, "--data-directory") is var at and >= 0 && at + 1 < args.Length)
    Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", args[at + 1]);
Directory.CreateDirectory(Protocol.DataDirectory);
FileStream ownership;
try { ownership = new FileStream(Path.Combine(Protocol.DataDirectory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException) { return 2; }
using var owned = ownership;
var log = new LogFile(Protocol.LogsDirectory, "engine");
log.Info("Engine", $"Starting the IDM-Rea engine {typeof(EngineHost).Assembly.GetName().Version?.ToString(3)} on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Crash("Engine", "The engine closed on an error it didn't expect", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) => log.Error("Engine", "A background task failed and nothing was waiting for it", e.Exception);
// Not disposed: ProcessExit runs after the last line here, and cancelling a disposed source crashed every exit.
var stopped = new CancellationTokenSource();
var finished = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.Cancel(); };
// Windows shutdown or a kill request: stop serving and let the manager save running jobs as paused by the engine.
AppDomain.CurrentDomain.ProcessExit += (_, _) => { stopped.Cancel(); finished.Wait(TimeSpan.FromSeconds(5)); };
try
{
#if WINDOWS
    // Started from the app or with "--tray" at sign-in: either way there is no window, only the tray icon.
    using var windows = new IDMRea.Engine.Windows.WindowsShell();
    IEngineShell shell = windows;
#else
    IEngineShell shell = new NullShell();
#endif
    // Video tools and direct-file checks: redirects are followed by RedirectHandler, so cookies never reach another host.
    using var web = new HttpClient(new RedirectHandler(new System.Net.Http.SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) }))
        { Timeout = Timeout.InfiniteTimeSpan };
    var tools = new VideoTools(Protocol.DataDirectory, web, ToolSources.Official, TimeProvider.System);
    var video = new VideoSupport(tools, new ProcessRunner(), Path.Combine(Protocol.DataDirectory, "temp"), web);
    await using var manager = new DownloadManager(Protocol.DataDirectory, new ManagerOptions(Recycle: shell.Recycle, Video: new VideoServices(video, video)));
#if WINDOWS
    windows.Attach(manager, stopped);
#endif
    await new EngineHost(manager, shell, video, log).RunAsync(stopped.Token);
    log.Info("Engine", "Stopped");
}
finally { finished.Set(); }
return 0;
