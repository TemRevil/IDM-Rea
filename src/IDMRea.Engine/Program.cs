using IDMRea.Contracts;
using IDMRea.Core;
using IDMRea.Engine;

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
    IEngineShell shell = new NullShell();
    await using var manager = new DownloadManager(Protocol.DataDirectory, new ManagerOptions(Recycle: shell.Recycle));
    await new EngineHost(manager, shell).RunAsync(stopped.Token);
}
finally { finished.Set(); }
return 0;
