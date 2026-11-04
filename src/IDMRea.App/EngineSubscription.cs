using System.Text;
using System.Text.Json;
using IDMRea.Contracts;
namespace IDMRea.App;

/// <summary>
/// Keeps one <c>subscribe</c> line open to the engine and raises every update it pushes.
/// Reconnects every 2 s while the engine is away; stopped while the window is minimised.
/// Events are raised on a background thread.
/// </summary>
public sealed class EngineSubscription
{
    private CancellationTokenSource? running;
    public event Action<EngineResponse>? Received;
    public event Action<bool>? ConnectionChanged;
    public bool Connected { get; private set; }

    public Task StartAsync(CancellationToken token)
    {
        Stop();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        running = cts;
        return Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref running, null);
        cts?.Cancel();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(TimeSpan.FromSeconds(2));
                await using var pipe = await EngineClient.ConnectAsync(connect.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new EngineRequest("subscribe", Version: Protocol.CurrentVersion), Protocol.Json).AsMemory(), token);
                while (!token.IsCancellationRequested)
                {
                    var line = await Protocol.ReadLineAsync(reader, 16 * 1024 * 1024, token);
                    var update = JsonSerializer.Deserialize<EngineResponse>(line, Protocol.Json);
                    if (update is not { Ok: true }) continue;
                    SetConnected(true);
                    Received?.Invoke(update);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or TimeoutException or UnauthorizedAccessException) { }
            SetConnected(false);
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); } catch (OperationCanceledException) { break; }
        }
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        ConnectionChanged?.Invoke(value);
    }
}
