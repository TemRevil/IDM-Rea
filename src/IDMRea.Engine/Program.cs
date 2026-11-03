using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using IDMRea.Contracts;
using IDMRea.Core;

Directory.CreateDirectory(Protocol.DataDirectory);
FileStream ownership;
try { ownership = new FileStream(Path.Combine(Protocol.DataDirectory, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException) { return 2; }
using var owned = ownership;
await using var manager = new DownloadManager(Protocol.DataDirectory);
using var stopped = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.Cancel(); };
while (!stopped.IsCancellationRequested)
{
    using var pipe = new NamedPipeServerStream(Protocol.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try
    {
        await pipe.WaitForConnectionAsync(stopped.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        EngineResponse response; var shutdown = false;
        try
        {
            var line = await Protocol.ReadLineAsync(reader, 65536, timeout.Token);
            var request = JsonSerializer.Deserialize<EngineRequest>(line, Protocol.Json) ?? throw new InvalidDataException("Missing request.");
            if (request.Version != 1) throw new InvalidDataException("Unsupported protocol version.");
            switch (request.Command)
            {
                case "list": break;
                case "add": await manager.AddAsync(request.Url ?? "", request.Folder ?? ""); break;
                case "pause": await manager.ControlAsync(request.Id ?? throw new ArgumentException("Missing job ID.")); break;
                case "resume": await manager.ControlAsync(request.Id ?? throw new ArgumentException("Missing job ID."), resume: true); break;
                case "cancel": await manager.ControlAsync(request.Id ?? throw new ArgumentException("Missing job ID."), cancel: true); break;
                case "shutdown": shutdown = true; break;
                default: throw new ArgumentException("Unknown command.");
            }
            response = new(true, manager.Snapshot());
        }
        catch (Exception ex) { response = new(false, Error: ex.Message); }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Protocol.Json).AsMemory(), timeout.Token);
        if (shutdown) stopped.Cancel();
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
}
return 0;
