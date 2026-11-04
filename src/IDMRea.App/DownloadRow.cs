using System.ComponentModel;
using IDMRea.Contracts;
namespace IDMRea.App;

/// <summary>One download as the list shows it. Updated in place so selection and focus survive refreshes.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class DownloadRow(DownloadJob job) : INotifyPropertyChanged
{
    private long speed;
    private int retryAttempts = 5;
    public DownloadJob Job { get; private set; } = job;
    public Guid Id => Job.Id;
    public string Name => Job.FileName;
    public DownloadStatus Status => Job.Status;
    public long Speed => speed;
    public string StatusLine => Text.StatusLine(Job, speed, DateTimeOffset.Now, retryAttempts);
    public double Progress => Job.TotalBytes > 0 ? Math.Clamp(100.0 * Job.ReceivedBytes / Job.TotalBytes.Value, 0, 100) : Job.Status == DownloadStatus.Completed ? 100 : 0;
    public bool Indeterminate => Job.Status == DownloadStatus.Finalizing || (Job.TotalBytes is null && Job.Status == DownloadStatus.Downloading);
    public bool CanPause => Job.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Retrying;
    public bool CanResume => Job.Status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool CanRetry => Job.Status is DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.Retrying;
    public bool CanCancel => Job.Status is not (DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Finalizing);
    public bool IsCompleted => Job.Status == DownloadStatus.Completed;
    public bool IsFailed => Job.Status == DownloadStatus.Failed;
    public bool ShowProgress => Job.Status is not (DownloadStatus.Completed or DownloadStatus.Cancelled);
    public string CategoryGlyph => Glyph(Job.Category);
    public string ToggleLabel => CanPause ? "Pause" : "Resume";
    public string ToggleGlyph => CanPause ? "" : "";
    public bool ToggleEnabled => CanPause || CanResume;

    public static string Glyph(Category category) => category switch
    {
        Category.Video => "", Category.Music => "", Category.Documents => "",
        Category.Programs => "", Category.Archives => "", Category.Images => "", _ => "",
    };

    /// <summary>The name screen readers and UI Automation read for the list item.</summary>
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(DownloadJob value, long bytesPerSecond, int attempts)
    {
        var changed = !ReferenceEquals(value, Job) && value != Job || bytesPerSecond != speed || attempts != retryAttempts;
        Job = value; speed = bytesPerSecond; retryAttempts = attempts;
        if (changed) Refresh();
    }

    /// <summary>Re-reads every bound value, e.g. once a second for a retry countdown.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));
}
