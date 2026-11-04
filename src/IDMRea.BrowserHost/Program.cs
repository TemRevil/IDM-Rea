using System.Diagnostics;
using IDMRea.BrowserHost;
using IDMRea.Contracts;

// Started by the browser for the extension's connection. It speaks to the extension on stdin/stdout and to the engine
// over its pipe. The browser gives it none of the app's environment, so the data folder comes from "Add to browser".
var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMRea");
if (HostHandler.TargetDataFolder(home) is { } target) Environment.SetEnvironmentVariable("IDMREA_DATA_DIRECTORY", target);

var client = new EngineClient();
var engine = Path.Combine(AppContext.BaseDirectory, "IDMRea.Engine.exe");
var handler = new HostHandler(async request => { await client.EnsureStartedAsync(engine); return await client.SendAsync(request); }, OpenApp);

await using var input = Console.OpenStandardInput();
await using var output = Console.OpenStandardOutput();
using var writing = new SemaphoreSlim(1);
var pending = new List<Task>();
try
{
    while (await NativeMessaging.ReadAsync(input) is { } message)
        pending.Add(Task.Run(async () =>
        {
            object reply;
            using (message) reply = await handler.HandleAsync(message.RootElement);
            await writing.WaitAsync();
            try { await NativeMessaging.WriteAsync(output, reply); } finally { writing.Release(); }
        }));
}
catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or System.Text.Json.JsonException) { return 1; }
await Task.WhenAll(pending);
return 0;

// The app sits one folder above the engine folder this host is published into.
static void OpenApp()
{
    var app = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "IDMRea.App.exe"));
    if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! })?.Dispose();
}
