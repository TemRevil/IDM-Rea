using System.ComponentModel;
using System.Globalization;
using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
namespace IDMRea.App.Views;

/// <summary>Everything about one download, with every action on it.</summary>
public sealed partial class DetailsPane : UserControl
{
    private DownloadRow? row;
    public event Action<DownloadRow, string>? ActionRequested;
    public event Action? CloseRequested;

    public DetailsPane() => InitializeComponent();

    public DownloadRow? Row
    {
        get => row;
        set
        {
            if (row != null) row.PropertyChanged -= Row_PropertyChanged;
            row = value;
            if (row != null) { row.PropertyChanged += Row_PropertyChanged; Refresh(); }
        }
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private static string When(DateTimeOffset? at) =>
        at is { } t && t != default ? t.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) : "-";

    private void Refresh()
    {
        if (row is null) return;
        var job = row.Job;
        KindIcon.Glyph = row.CategoryGlyph;
        NameText.Text = row.Name;
        StatusText.Text = row.StatusLine;
        Parts.Show(job);
        PartsText.Text = job.Parts is { Count: > 1 } parts ? $"{parts.Count} parts" : "One connection";
        SizeText.Text = job.TotalBytes is long total ? Text.Size(total) : "Unknown";
        ReceivedText.Text = job.TotalBytes is long t2 && t2 > 0
            ? $"{Text.Size(job.ReceivedBytes)} ({(100.0 * job.ReceivedBytes / t2).ToString("0", CultureInfo.InvariantCulture)}%)"
            : Text.Size(job.ReceivedBytes);
        var active = job.Status == DownloadStatus.Downloading;
        SpeedText.Text = active && row.Speed > 0 ? Text.Speed(row.Speed) : "-";
        LeftText.Text = active && row.Speed > 0 && job.TotalBytes is long t3
            ? Text.TimeLeft(TimeSpan.FromSeconds((double)Math.Max(0, t3 - job.ReceivedBytes) / row.Speed)) : "-";
        ResumableText.Text = job.Resumable ? "Yes" : "No";
        AddedText.Text = When(job.CreatedAt);
        FinishedText.Text = When(job.CompletedAt);
        AttemptsText.Text = job.Attempts.ToString(CultureInfo.InvariantCulture);
        ChecksumLabel.Visibility = ChecksumText.Visibility = job.ExpectedSha256 is null ? Visibility.Collapsed : Visibility.Visible;
        ChecksumText.Text = job.Verified ? "Matches the site's"
            : job.Status == DownloadStatus.Failed && job.ErrorKind == ErrorKind.ChecksumMismatch ? "Doesn't match the site's"
            : "Checked when the download finishes";
        UrlText.Text = job.Url;
        PathText.Text = job.Destination;
        ErrorInfo.IsOpen = job.Status == DownloadStatus.Failed;
        ErrorInfo.Message = job.Status == DownloadStatus.Failed ? Text.Describe(job.ErrorKind == ErrorKind.None ? ErrorKind.Other : job.ErrorKind, job.HttpStatus) : "";

        OpenButton.Visibility = row.IsCompleted ? Visibility.Visible : Visibility.Collapsed;
        ToggleButton.Visibility = row.ToggleEnabled ? Visibility.Visible : Visibility.Collapsed;
        ToggleIcon.Glyph = row.ToggleGlyph;
        ToggleText.Text = row.ToggleLabel;
        AutomationProperties.SetName(ToggleButton, row.ToggleLabel);
        RetryButton.Visibility = row.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = row.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.Visibility = row.IsCompleted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (row != null && sender is FrameworkElement { Tag: string action }) ActionRequested?.Invoke(row, action);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
