using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace IDMRea.Contracts;

/// <summary>One request, one reply over the engine pipe. Starts the engine when it is not running.</summary>
public sealed class EngineClient
{
    public static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(token); return pipe; }
        catch { await pipe.DisposeAsync(); throw; }
    }

    public async Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken token = default)
    {
        // probe and add check the link first, which the engine allows up to Protocol.ProbeLimit.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Command is "probe" or "add" ? Protocol.ProbeLimit + TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(20));
        await using var pipe = await ConnectAsync(deadline.Token);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Protocol.Json).AsMemory(), deadline.Token);
        return JsonSerializer.Deserialize<EngineResponse>(await Protocol.ReadLineAsync(reader, 16 * 1024 * 1024, deadline.Token), Protocol.Json) ?? throw new IOException("Empty engine response.");
    }

    /// <param name="enginePath">The engine to start; by default the one in the app's <c>engine</c> folder.</param>
    public async Task EnsureStartedAsync(string? enginePath = null)
    {
        using var probe = new CancellationTokenSource(500);
        try { await SendAsync(new("list"), probe.Token); return; } catch (OperationCanceledException) { }
        var path = enginePath ?? Path.Combine(AppContext.BaseDirectory, "engine", "IDMRea.Engine.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("The download engine is missing from the app folder.", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = await SendAsync(new("list"), startup.Token);
        if (!response.Ok) throw new IOException(response.Error ?? "The engine could not start.");
    }
}
