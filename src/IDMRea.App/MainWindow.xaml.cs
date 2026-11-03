using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
namespace IDMRea.App;
public partial class MainWindow : Window
{
    private readonly EngineClient client = new();
    private readonly ObservableCollection<DownloadRow> rows = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly SemaphoreSlim commands = new(1);
    private bool busy, stopped, ready;
    public MainWindow()
    {
        InitializeComponent(); AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        JobsList.ItemsSource = rows; timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => timer.Stop();
        _ = InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        try
        {
            await client.EnsureStartedAsync();
            ready = await ExecuteAsync(new("list"));
            AddButton.IsEnabled = StopButton.IsEnabled = ready;
            if (ready) timer.Start();
        }
        catch (Exception ex) { ShowError(ex.Message); StatusLabel.Text = "Engine unavailable"; }
    }
    private void ShowError(string error) { ErrorBar.Message = error; ErrorBar.IsOpen = true; }
    private async Task<bool> ExecuteAsync(EngineRequest request)
    {
        await commands.WaitAsync();
        try
        {
            var result = await client.SendAsync(request);
            if (!result.Ok) { ShowError(result.Error ?? "Engine error."); return false; }
            Apply(result.Jobs ?? []);
            if (request.Command != "list") ErrorBar.IsOpen = false;
            return true;
        }
        catch (Exception ex) { ShowError(ex.Message); return false; }
        finally { commands.Release(); }
    }
    private async Task RefreshAsync() { if (busy || stopped) return; busy = true; try { await ExecuteAsync(new("list")); } finally { busy = false; } }
    private void Apply(IReadOnlyList<DownloadJob> jobs)
    {
        foreach (var job in jobs) { var row = rows.FirstOrDefault(r => r.Id == job.Id); if (row == null) rows.Add(new(job)); else row.Update(job); }
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountLabel.Text = $"{jobs.Count} file{(jobs.Count == 1 ? "" : "s")}";
        var active = jobs.Count(j => j.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        StatusLabel.Text = active > 0 ? $"{active} active · Downloads continue when you close this window" : "Ready · Up to 3 simultaneous downloads";
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (stopped || !ready || !AddButton.IsEnabled) return;
        AddButton.IsEnabled = false;
        try
        {
            if (await ExecuteAsync(new("add", UrlBox.Text.Trim(), FolderBox.Text.Trim()))) UrlBox.Text = "";
        }
        finally { AddButton.IsEnabled = !stopped && ready; }
    }
    private void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    { if (e.Key == Windows.System.VirtualKey.Enter) Add_Click(sender, e); }
    private async void Pause_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("pause", Id: (Guid)((Button)sender).Tag)); }
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("resume", Id: (Guid)((Button)sender).Tag)); }
    private async void Cancel_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("cancel", Id: (Guid)((Button)sender).Tag)); }
    private void Open_Click(object sender, RoutedEventArgs e) { try { Directory.CreateDirectory(FolderBox.Text); Process.Start(new ProcessStartInfo(FolderBox.Text) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        if (await ExecuteAsync(new("shutdown")))
        {
            timer.Stop(); stopped = true; ready = false;
            AddButton.IsEnabled = false; JobsList.IsEnabled = false;
            StatusLabel.Text = "Engine stopping · Reopen the app to resume your saved downloads.";
        }
        else StopButton.IsEnabled = true;
    }
}
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class DownloadRow(DownloadJob job) : INotifyPropertyChanged
{
    private DownloadJob job = job;
    public Guid Id => job.Id;
    public string Name => Path.GetFileName(job.Destination);
    public string Detail => $"{job.Status}  ·  {job.ReceivedBytes / 1048576.0:F1} / {(job.TotalBytes is long total ? (total / 1048576.0).ToString("F1") : "?")} MB";
    public double Progress => job.TotalBytes > 0 ? Math.Clamp(100.0 * job.ReceivedBytes / job.TotalBytes.Value, 0, 100) : job.Status == DownloadStatus.Completed ? 100 : 0;
    public bool Indeterminate => job.TotalBytes is null && job.Status == DownloadStatus.Downloading;
    public bool CanPause => job.Status is DownloadStatus.Queued or DownloadStatus.Downloading;
    public bool CanResume => job.Status is DownloadStatus.Paused or DownloadStatus.Failed;
    public bool CanCancel => job.Status is not (DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Finalizing);
    public string Error => job.Error ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(DownloadJob value) { job = value; PropertyChanged?.Invoke(this, new(null)); }
}
