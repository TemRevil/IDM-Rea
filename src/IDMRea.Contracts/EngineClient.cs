using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
namespace IDMRea.Contracts;

/// <summary>One request, one reply over the engine pipe. Starts the engine when it is not running.</summary>
public sealed class EngineClient
{
    /// <summary>A call that should be quick taking longer than this is logged as slow.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(1.5);

    /// <summary>Where calls that failed (the engine unreachable or hanging up) or were slow are noted; none if null.</summary>
    public LogFile? Log { get; init; }

    public static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(token); return pipe; }
        catch { await pipe.DisposeAsync(); throw; }
    }

    public async Task<EngineResponse> SendAsync(EngineRequest request, CancellationToken token = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await SendQuietlyAsync(request, token);
            var took = Stopwatch.GetElapsedTime(started);
            if (took > Slow && Protocol.NetworkLimit(request.Command) is null) Log?.Write(LogLevel.Slow, "Engine", $"{request.Command} took {LogFile.Seconds(took)}");
            return response;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Log?.Warn("Engine", $"{request.Command} didn't get an answer after {LogFile.Seconds(Stopwatch.GetElapsedTime(started))}", ex);
            throw;
        }
    }

    private static async Task<EngineResponse> SendQuietlyAsync(EngineRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter((Protocol.NetworkLimit(request.Command) ?? TimeSpan.FromSeconds(5)) + TimeSpan.FromSeconds(15));
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
        try
        {
            var path = enginePath ?? Path.Combine(AppContext.BaseDirectory, "engine", "IDMRea.Engine.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("The download engine is missing from the app folder.", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true });
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await SendQuietlyAsync(new("list"), startup.Token);
            if (!response.Ok) throw new IOException(response.Error ?? "The engine could not start.");
        }
        catch (Exception ex)
        {
            Log?.Error("Engine", "The download engine didn't start", ex);
            throw;
        }
    }
}
