using System.Buffers.Binary;
using System.Text.Json;
using IDMRea.Contracts;
namespace IDMRea.BrowserHost;

/// <summary>The browser's native messaging framing: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.</summary>
public static class NativeMessaging
{
    /// <summary>The browser never sends the host more than this in practice; anything larger is refused.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>The next message, or null when the browser has closed the connection.</summary>
    public static async Task<JsonDocument?> ReadAsync(Stream input, CancellationToken token = default)
    {
        var header = new byte[4];
        var read = await FillAsync(input, header, token);
        if (read == 0) return null;
        if (read < header.Length) throw new EndOfStreamException("The message header was cut off.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxMessageBytes) throw new InvalidDataException("The message is too large.");
        var body = new byte[length];
        if (await FillAsync(input, body, token) < length) throw new EndOfStreamException("The message was cut off.");
        return JsonDocument.Parse(body);
    }

    public static async Task WriteAsync(Stream output, object message, CancellationToken token = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Protocol.Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await output.WriteAsync(header, token);
        await output.WriteAsync(body, token);
        await output.FlushAsync(token);
    }

    private static async Task<int> FillAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), token);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
