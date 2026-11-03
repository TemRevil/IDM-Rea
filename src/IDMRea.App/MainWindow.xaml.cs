using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace IDMRea.App;
public partial class MainWindow : Window
{
    private readonly EngineClient client = new();
    private readonly ObservableCollection<DownloadRow> rows = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool busy, stopped;
    public MainWindow()
    {
        InitializeComponent(); AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 800));
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        JobsList.ItemsSource = rows; timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => timer.Stop();
        _ = InitializeAsync();
    }
    private async Task InitializeAsync()
    { try { await client.EnsureStartedAsync(); await RefreshAsync(); timer.Start(); } catch (Exception ex) { ShowError(ex.Message); } }
    private void ShowError(string error) { ErrorBar.Message = error; ErrorBar.IsOpen = true; }
    private async Task ExecuteAsync(EngineRequest request)
    { try { var result = await client.SendAsync(request); if (!result.Ok) ShowError(result.Error ?? "Engine error."); else Apply(result.Jobs ?? []); } catch (Exception ex) { ShowError(ex.Message); } }
    private async Task RefreshAsync() { if (busy || stopped) return; busy = true; try { await ExecuteAsync(new("list")); } finally { busy = false; } }
    private void Apply(IReadOnlyList<DownloadJob> jobs)
    {
        foreach (var job in jobs) { var row = rows.FirstOrDefault(r => r.Id == job.Id); if (row == null) rows.Add(new(job)); else row.Update(job); }
        EmptyState.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void Add_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("add", UrlBox.Text, FolderBox.Text)); }
    private async void Pause_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("pause", Id: (Guid)((Button)sender).Tag)); }
    private async void Resume_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("resume", Id: (Guid)((Button)sender).Tag)); }
    private async void Cancel_Click(object sender, RoutedEventArgs e) { if (!stopped) await ExecuteAsync(new("cancel", Id: (Guid)((Button)sender).Tag)); }
    private void Open_Click(object sender, RoutedEventArgs e) { try { Directory.CreateDirectory(FolderBox.Text); Process.Start(new ProcessStartInfo(FolderBox.Text) { UseShellExecute = true }); } catch (Exception ex) { ShowError(ex.Message); } }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    { timer.Stop(); await ExecuteAsync(new("shutdown")); stopped = true; AddButton.IsEnabled = false; JobsList.IsEnabled = false; StopButton.IsEnabled = false; }
}
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class DownloadRow(DownloadJob job) : INotifyPropertyChanged
{
    private DownloadJob job = job;
    public Guid Id => job.Id;
    public string Name => Path.GetFileName(job.Destination);
    public string Detail => $"{job.Status}  ·  {job.ReceivedBytes / 1048576.0:F1} / {(job.TotalBytes is long total ? (total / 1048576.0).ToString("F1") : "?")} MB";
    public double Progress => job.TotalBytes > 0 ? Math.Clamp(100.0 * job.ReceivedBytes / job.TotalBytes.Value, 0, 100) : 0;
    public string Error => job.Error ?? "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(DownloadJob value) { job = value; PropertyChanged?.Invoke(this, new(null)); }
}
