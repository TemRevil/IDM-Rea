using System.Globalization;
using System.Text.RegularExpressions;
namespace IDMRea.Core;

public sealed record HlsVariant(Uri Url, long Bandwidth, int? Height);

/// <summary>An AES-128 key: where to fetch it, and its IV when the list names one (otherwise the segment's sequence number).</summary>
public sealed record HlsKey(Uri Url, byte[]? Iv)
{
    public bool Equals(HlsKey? other) => other is not null && Url == other.Url && (Iv ?? []).AsSpan().SequenceEqual(other.Iv ?? []) && (Iv is null) == (other.Iv is null);
    public override int GetHashCode() => Url.GetHashCode();
}

/// <summary>One piece of a stream. <see cref="Offset"/> and <see cref="Length"/> are set for a byte range of a larger file.</summary>
public sealed record HlsSegment(Uri Url, long? Offset, long? Length, HlsKey? Key, long Sequence);

/// <summary>Reads HLS lists (RFC 8216): a master list of qualities, or a media list of segments.</summary>
public static partial class Hls
{
    public static bool IsMaster(string text) => Lines(text).Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));

    public static IReadOnlyList<HlsVariant> ParseMaster(string text, Uri baseUri)
    {
        var variants = new List<HlsVariant>();
        Dictionary<string, string>? pending = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) pending = Attributes(line["#EXT-X-STREAM-INF:".Length..]);
            else if (pending != null && !line.StartsWith('#'))
            {
                long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth);
                int? height = pending.GetValueOrDefault("RESOLUTION")?.Split('x') is [_, var h] && int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
                variants.Add(new HlsVariant(new Uri(baseUri, line), bandwidth, height));
                pending = null;
            }
        }
        return variants;
    }

    /// <summary>The segments in play order, with the EXT-X-MAP header (if any) first at sequence 0.</summary>
    /// <exception cref="VideoException">The list never ends (a live stream), or its segments are DRM-protected.</exception>
    public static IReadOnlyList<HlsSegment> ParseMedia(string text, Uri baseUri)
    {
        var lines = Lines(text).ToList();
        if (!lines.Contains("#EXT-X-ENDLIST")) throw new VideoException("This is a live stream. IDM-Rea downloads videos that have finished.");
        var segments = new List<HlsSegment>();
        long sequence = 0; HlsKey? key = null; long? rangeLength = null, rangeOffset = null;
        var nextOffset = new Dictionary<Uri, long>();
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                sequence = long.Parse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], NumberStyles.None, CultureInfo.InvariantCulture);
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)) key = Key(Attributes(line["#EXT-X-KEY:".Length..]), baseUri);
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var map = Attributes(line["#EXT-X-MAP:".Length..]);
                var url = new Uri(baseUri, map["URI"]);
                var (length, offset) = Range(map.GetValueOrDefault("BYTERANGE"));
                segments.Add(new HlsSegment(url, length is null ? null : offset ?? 0, length, key, 0));
                if (length is long l) nextOffset[url] = (offset ?? 0) + l;
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal)) (rangeLength, rangeOffset) = Range(line["#EXT-X-BYTERANGE:".Length..]);
            else if (!line.StartsWith('#'))
            {
                var url = new Uri(baseUri, line);
                long? offset = null;
                if (rangeLength is long length)
                {
                    offset = rangeOffset ?? nextOffset.GetValueOrDefault(url);
                    nextOffset[url] = offset.Value + length;
                }
                segments.Add(new HlsSegment(url, offset, rangeLength, key, sequence++));
                rangeLength = rangeOffset = null;
            }
        }
        return segments;
    }

    private static HlsKey? Key(Dictionary<string, string> attributes, Uri baseUri)
    {
        var method = attributes.GetValueOrDefault("METHOD", "NONE");
        if (method == "NONE") return null;
        var format = attributes.GetValueOrDefault("KEYFORMAT", "identity");
        if (method != "AES-128" || format != "identity") throw new VideoException("This video is protected against copying (DRM), so it can't be downloaded.");
        byte[]? iv = null;
        if (attributes.TryGetValue("IV", out var hex))
        {
            hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
            iv = Convert.FromHexString(hex.PadLeft(32, '0'));
        }
        return new HlsKey(new Uri(baseUri, attributes["URI"]), iv);
    }

    /// <summary>"length[@offset]".</summary>
    private static (long? Length, long? Offset) Range(string? value)
    {
        if (string.IsNullOrEmpty(value)) return (null, null);
        var parts = value.Split('@');
        return (long.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture),
            parts.Length > 1 ? long.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture) : null);
    }

    private static Dictionary<string, string> Attributes(string list) =>
        AttributePattern().Matches(list).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);

    [GeneratedRegex("""([A-Z0-9-]+)=(?:"([^"]*)"|([^,]*))""")]
    private static partial Regex AttributePattern();

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
