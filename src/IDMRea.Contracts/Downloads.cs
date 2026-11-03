using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDMRea.Contracts;
public enum DownloadStatus { Queued, Downloading, Paused, Finalizing, Completed, Failed, Cancelled }
public sealed record DownloadJob(Guid Id, string Url, string Destination, string PartialPath,
    DownloadStatus Status, long ReceivedBytes = 0, long? TotalBytes = null, string? ETag = null,
    DateTimeOffset? LastModified = null, string? Error = null, DateTimeOffset UpdatedAt = default,
    string? CompletionSha256 = null);
public sealed record EngineRequest(string Command, string? Url = null, string? Folder = null, Guid? Id = null, int Version = 1);
public sealed record EngineResponse(bool Ok, IReadOnlyList<DownloadJob>? Jobs = null, string? Error = null, int Version = 1);
public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public static string DataDirectory => Path.GetFullPath(Environment.GetEnvironmentVariable("IDMREA_DATA_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMRea"));
    public static string PipeName => "IDMRea." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory)))[..20];
    public static async Task<string> ReadLineAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0)
        {
            if (buffer[0] == '\n') return result.ToString();
            if (result.Length >= limit) throw new InvalidDataException("Protocol line too long.");
            result.Append(buffer[0]);
        }
        throw new EndOfStreamException();
    }
}
