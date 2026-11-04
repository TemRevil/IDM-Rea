using IDMRea.Contracts;
namespace IDMRea.Core;

/// <summary>Decides how a file is cut into parts, and which running part to split when a connection frees up.</summary>
public static class PartPlanner
{
    /// <summary>A part is split only when more than this is left of it.</summary>
    public const long SplitThreshold = 2 * 1024 * 1024;

    public static IReadOnlyList<DownloadPart> Initial(long total, int maxParts, long minPartSize)
    {
        if (total <= 0) return [];
        var count = (int)Math.Clamp(total / Math.Max(1, minPartSize), 1, Math.Max(1, maxParts));
        var size = total / count; var parts = new DownloadPart[count];
        for (var i = 0; i < count; i++)
            parts[i] = new DownloadPart(i * size, i == count - 1 ? total - 1 : (i + 1) * size - 1, 0);
        return parts;
    }

    public static int? PickToSplit(IReadOnlyList<DownloadPart> parts, IReadOnlySet<int> active)
    {
        int? best = null;
        foreach (var i in active)
            if (parts[i].Remaining > SplitThreshold && (best == null || parts[i].Remaining > parts[best.Value].Remaining)) best = i;
        return best;
    }
}
