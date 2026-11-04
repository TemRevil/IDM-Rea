using System.Collections.ObjectModel;
using System.ComponentModel;
using IDMRea.Contracts;
namespace IDMRea.App;

/// <summary>Everything the window shows, built from the engine's updates. UI thread only.</summary>
public sealed class AppState : INotifyPropertyChanged
{
    private readonly Dictionary<Guid, DownloadRow> rows = [];
    private string filter = DownloadViews.All, search = "";
    private SortMode sort = SortMode.Newest;

    public ObservableCollection<DownloadRow> Visible { get; } = [];
    public IReadOnlyDictionary<string, int> Counts { get; private set; } = DownloadViews.Counts([]);
    public EngineTotals Totals { get; private set; } = new(0, 0);
    public EngineSettings Settings { get; private set; } = new();
    public bool HasSettings { get; private set; }
    public IEnumerable<DownloadRow> All => rows.Values;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<EngineSettings>? SettingsChanged;

    public string Filter { get => filter; set { if (filter == value) return; filter = value; Rebuild(); Raise(nameof(Filter)); } }
    public string Search { get => search; set { if (search == value) return; search = value ?? ""; Rebuild(); Raise(nameof(Search)); } }
    public SortMode Sort { get => sort; set { if (sort == value) return; sort = value; Rebuild(); Raise(nameof(Sort)); } }

    public DownloadRow? Find(Guid id) => rows.GetValueOrDefault(id);

    public void Apply(EngineResponse update)
    {
        var speeds = update.Speeds ?? new Dictionary<Guid, long>();
        var attempts = (update.Settings ?? Settings).RetryAttempts;
        if (update.Settings is { } settings)
        {
            Settings = settings; HasSettings = true;
            SettingsChanged?.Invoke(settings);
        }
        if (update.Full)
            foreach (var gone in rows.Keys.Except(update.Jobs?.Select(j => j.Id) ?? []).ToList()) rows.Remove(gone);
        foreach (var id in update.Removed ?? []) rows.Remove(id);
        foreach (var job in update.Jobs ?? [])
        {
            if (rows.TryGetValue(job.Id, out var row)) row.Update(job, speeds.GetValueOrDefault(job.Id), attempts);
            else { row = new DownloadRow(job); row.Update(job, speeds.GetValueOrDefault(job.Id), attempts); rows[job.Id] = row; }
        }
        // Speeds come for every active job; rows that dropped out of the speed list go back to zero.
        var updated = (update.Jobs ?? []).Select(j => j.Id).ToHashSet();
        foreach (var row in rows.Values) if (!updated.Contains(row.Id)) row.Update(row.Job, speeds.GetValueOrDefault(row.Id), attempts);
        if (update.Totals is { } totals) Totals = totals;
        Counts = DownloadViews.Counts(rows.Values.Select(r => r.Job));
        Rebuild();
        Raise(null);
    }

    /// <summary>Brings <see cref="Visible"/> in line with the filter, search and sort while keeping existing row objects.</summary>
    private void Rebuild()
    {
        var target = DownloadViews.Sort(rows.Values.Select(r => r.Job).Where(j => DownloadViews.Matches(j, filter, search)), sort)
            .Select(j => rows[j.Id]).ToList();
        var keep = target.ToHashSet();
        for (var i = Visible.Count - 1; i >= 0; i--) if (!keep.Contains(Visible[i])) Visible.RemoveAt(i);
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Visible.Count && ReferenceEquals(Visible[i], target[i])) continue;
            var at = Visible.IndexOf(target[i]);
            if (at >= 0) Visible.Move(at, i); else Visible.Insert(i, target[i]);
        }
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new(name));
}
