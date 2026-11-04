using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDMRea.Contracts;

public sealed record ProbeResult(string Url, string ResolvedUrl, string FileName, long? TotalBytes,
    string? ETag, DateTimeOffset? LastModified, string? ContentType, Category Category, bool Resumable);

public sealed record EngineTotals(int Active, long BytesPerSecond);

public sealed record EngineRequest(string Command, string? Url = null, string? Folder = null, Guid? Id = null, int Version = 1,
    IReadOnlyList<Guid>? Ids = null, string? FileName = null, bool StartPaused = false, bool DeleteFile = false,
    EngineSettings? Settings = null);

public sealed record EngineResponse(bool Ok, IReadOnlyList<DownloadJob>? Jobs = null, string? Error = null, int Version = 1,
    bool Full = false, IReadOnlyList<Guid>? Removed = null, IReadOnlyDictionary<Guid, long>? Speeds = null,
    EngineTotals? Totals = null, EngineSettings? Settings = null, ProbeResult? Probe = null);

public static class Protocol
{
    public const int CurrentVersion = 2;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    /// <summary>The longest a link check may take; <c>probe</c> and <c>add</c> replies can take this long.</summary>
    public static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(30);
    public static string DataDirectory => Path.GetFullPath(Environment.GetEnvironmentVariable("IDMREA_DATA_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMRea"));
    public static string PipeName => "IDMRea." + FolderHash(DataDirectory);
    /// <summary>The app's single-instance key: one window per data folder, like the pipe and the engine lock.</summary>
    public static string InstanceKey => InstanceKeyFor(DataDirectory);
    public static string InstanceKeyFor(string dataDirectory) => "IDMRea.Main." + FolderHash(Path.GetFullPath(dataDirectory));
    private static string FolderHash(string folder) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..20];
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
