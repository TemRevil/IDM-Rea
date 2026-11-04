using System.Text.Json;
using IDMRea.Contracts;
namespace IDMRea.BrowserHost;

/// <summary>
/// Answers the extension. <c>hello</c>: is IDM-Rea there, and should it take over browser downloads.
/// <c>add</c>: download a link with the page's referrer, cookies and user agent (and the site's checksum, if any).
/// <c>open</c>: show the app. Every reply carries the message's <c>id</c> back.
/// </summary>
public sealed class HostHandler(Func<EngineRequest, Task<EngineResponse>> send, Action? openApp = null)
{
    public static string Version => typeof(HostHandler).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The data folder "Add to browser" chose, from <c>browser-target.txt</c> in <paramref name="folder"/>; null if none.</summary>
    public static string? TargetDataFolder(string folder)
    {
        var file = Path.Combine(folder, "browser-target.txt");
        if (!File.Exists(file)) return null;
        var target = File.ReadAllText(file).Trim();
        return target.Length == 0 ? null : target;
    }

    public async Task<object> HandleAsync(JsonElement message)
    {
        int? id = message.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
        try
        {
            switch (Text(message, "type"))
            {
                case "hello":
                    var settings = await send(new("getSettings", Version: Protocol.CurrentVersion));
                    if (!settings.Ok || settings.Settings is not { } s) return Failed(id, settings.Error);
                    return new { id, ok = true, version = Version, takeover = s.BrowserTakeover, minBytes = s.BrowserMinBytes };
                case "add":
                    var added = await send(new("add", Url: Text(message, "url"), FileName: Text(message, "fileName"), Version: Protocol.CurrentVersion,
                        Referrer: Text(message, "referrer"), Cookies: Text(message, "cookies"), UserAgent: Text(message, "userAgent"),
                        ExpectedSha256: Text(message, "sha256")));
                    return added.Ok ? new { id, ok = true } : Failed(id, added.Error);
                case "open":
                    openApp?.Invoke();
                    return new { id, ok = true };
                default:
                    return Failed(id, "Unknown message.");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(id, "IDM-Rea isn't responding. " + ex.Message);
        }
    }

    private static object Failed(int? id, string? error) => new { id, ok = false, error = error ?? "Something went wrong." };

    private static string? Text(JsonElement message, string name) =>
        message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
