namespace IDMRea.Contracts;

public enum SortMode { Newest, Name, Size, Status }

/// <summary>Which downloads each sidebar entry shows, how they are searched and sorted, and how many each holds.</summary>
public static class DownloadViews
{
    public const string All = "all", Downloading = "downloading", Queued = "queued", Completed = "completed", Failed = "failed";

    /// <summary>Every sidebar filter: the five status lists, then one per category (by its enum name).</summary>
    public static readonly IReadOnlyList<string> FilterNames =
        [All, Downloading, Queued, Completed, Failed, .. Enum.GetNames<Category>()];

    /// <summary>Cancelled downloads appear only under All.</summary>
    public static bool Matches(DownloadJob job, string filter, string search)
    {
        var inFilter = filter switch
        {
            All => true,
            Downloading => job.Status is DownloadStatus.Downloading or DownloadStatus.Retrying or DownloadStatus.Finalizing,
            Queued => job.Status is DownloadStatus.Queued or DownloadStatus.Paused,
            Completed => job.Status == DownloadStatus.Completed,
            Failed => job.Status == DownloadStatus.Failed,
            _ => Enum.TryParse<Category>(filter, out var category) && job.Category == category,
        };
        if (!inFilter) return false;
        var text = search.Trim();
        return text.Length == 0
            || job.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || job.Url.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, int> Counts(IEnumerable<DownloadJob> jobs)
    {
        var list = jobs as IReadOnlyCollection<DownloadJob> ?? jobs.ToList();
        return FilterNames.ToDictionary(f => f, f => list.Count(j => Matches(j, f, "")));
    }

    private static int Rank(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading => 0, DownloadStatus.Retrying => 1, DownloadStatus.Finalizing => 2,
        DownloadStatus.Queued => 3, DownloadStatus.Paused => 4, DownloadStatus.Failed => 5,
        DownloadStatus.Completed => 6, _ => 7,
    };

    public static IReadOnlyList<DownloadJob> Sort(IEnumerable<DownloadJob> jobs, SortMode mode)
    {
        var newest = jobs.OrderByDescending(j => j.CreatedAt).ThenBy(j => j.Id);
        return (mode switch
        {
            SortMode.Name => jobs.OrderBy(j => j.FileName, StringComparer.OrdinalIgnoreCase).ThenByDescending(j => j.CreatedAt),
            SortMode.Size => jobs.OrderBy(j => j.TotalBytes is null).ThenByDescending(j => j.TotalBytes ?? 0).ThenByDescending(j => j.CreatedAt),
            SortMode.Status => jobs.OrderBy(j => Rank(j.Status)).ThenByDescending(j => j.CreatedAt),
            _ => newest,
        }).ToList();
    }
}
