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
using var stopped = new CancellationTokenSource();
using var finished = new ManualResetEventSlim();
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
    await using var manager = new DownloadManager(Protocol.DataDirectory, new ManagerOptions(Recycle: shell.Recycle));
#if WINDOWS
    windows.Attach(manager, stopped);
#endif
    await new EngineHost(manager, shell).RunAsync(stopped.Token);
}
finally { finished.Set(); }
return 0;
