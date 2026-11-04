using System.ComponentModel;
using System.Diagnostics;
using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
namespace IDMRea.App.Views;

public sealed partial class DownloadsPage : Page
{
    private static readonly Dictionary<string, (string Title, string Empty, string Glyph)> Filters = new()
    {
        [DownloadViews.All] = ("All downloads", "No downloads yet.", ""),
        [DownloadViews.Downloading] = ("Downloading", "Nothing is downloading.", ""),
        [DownloadViews.Queued] = ("Queued and paused", "Nothing is waiting.", ""),
        [DownloadViews.Completed] = ("Completed", "Finished downloads appear here.", ""),
        [DownloadViews.Failed] = ("Failed", "No failed downloads.", ""),
    };
    private static MainWindow Shell => MainWindow.Instance;
    private static AppState State => Shell.State;

    public DownloadsPage()
    {
        InitializeComponent();
        List.ItemsSource = State.Visible;
        State.PropertyChanged += State_PropertyChanged;
        Details.ActionRequested += (row, action) => _ = RunAsync(row, action);
        Details.CloseRequested += () => List.SelectedItems.Clear();
        Unloaded += (_, _) => State.PropertyChanged -= State_PropertyChanged;
        Loaded += (_, _) => UpdateView();
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateView();

    private void UpdateView()
    {
        var filter = State.Filter;
        var (title, empty, glyph) = Filters.TryGetValue(filter, out var known) ? known
            : (filter, $"No {filter.ToLowerInvariant()} files yet.", DownloadRow.Glyph(Enum.Parse<Category>(filter)));
        TitleText.Text = title;
        var nothing = State.Visible.Count == 0;
        EmptyState.Visibility = nothing ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = State.Search.Trim().Length > 0 ? "No downloads match your search." : empty;
        EmptyIcon.Glyph = glyph;
        EmptyAdd.Visibility = filter == DownloadViews.All && State.Search.Trim().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SortNewest.IsChecked = State.Sort == SortMode.Newest; SortName.IsChecked = State.Sort == SortMode.Name;
        SortSize.IsChecked = State.Sort == SortMode.Size; SortStatus.IsChecked = State.Sort == SortMode.Status;
        UpdateSelection();
    }

    public void Select(Guid id)
    {
        if (State.Find(id) is not { } row) return;
        if (!State.Visible.Contains(row)) { State.Search = ""; }
        List.SelectedItems.Clear();
        List.SelectedItem = row;
        List.ScrollIntoView(row);
    }

    private IReadOnlyList<DownloadRow> Selected => List.SelectedItems.OfType<DownloadRow>().ToList();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var selected = Selected;
        var bulk = selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BulkPause.Visibility = BulkResume.Visibility = BulkRemove.Visibility = BulkSeparator.Visibility = bulk;
        if (selected.Count == 1) { Details.Row = selected[0]; Details.Visibility = Visibility.Visible; }
        else { Details.Visibility = Visibility.Collapsed; Details.Row = null; }
    }

    // Row actions appear on pointer-over and keyboard focus; they stay in the accessibility tree either way.
    private static void ShowActions(object sender, bool on) { if (sender is Grid { Children.Count: 3 } grid) grid.Children[2].Opacity = on ? 1 : 0; }
    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowActions(sender, true);
    private void Row_PointerExited(object sender, PointerRoutedEventArgs e) => ShowActions(sender, false);
    private void Row_GotFocus(object sender, RoutedEventArgs e) => ShowActions(sender, true);
    private void Row_LostFocus(object sender, RoutedEventArgs e) => ShowActions(sender, false);

    private void RowToggle_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: DownloadRow row }) _ = RunAsync(row, "toggle"); }
    private void RowFolder_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: DownloadRow row }) _ = RunAsync(row, "folder"); }

    private void RowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: DownloadRow row } item) return;
        _ = RunAsync(row, item.Text switch { "Cancel" => "cancel", "Retry" => "retry", "Copy link" => "copy", "Remove from list" => "remove", _ => "delete" });
    }

    private async Task RunAsync(DownloadRow row, string action)
    {
        var job = row.Job;
        try
        {
            switch (action)
            {
                case "toggle": await SendAsync(new(row.CanPause ? "pause" : "resume", Id: job.Id)); break;
                case "retry": await SendAsync(new("retry", Id: job.Id)); break;
                case "cancel": await SendAsync(new("cancel", Id: job.Id)); break;
                case "remove": await SendAsync(new("remove", Id: job.Id)); break;
                case "delete": await DeleteFileAsync(row); break;
                case "copy":
                    var data = new DataPackage(); data.SetText(job.Url); Clipboard.SetContent(data); break;
                case "open":
                    Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true }); break;
                case "folder": ShowInFolder(job); break;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }

    private static void ShowInFolder(DownloadJob job)
    {
        var file = File.Exists(job.Destination) ? job.Destination : File.Exists(job.PartialPath) ? job.PartialPath : null;
        if (file != null) Process.Start("explorer.exe", $"/select,\"{file}\"");
        else if (Path.GetDirectoryName(job.Destination) is { } folder && Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
        else throw new IOException("The save folder no longer exists.");
    }

    private async Task DeleteFileAsync(DownloadRow row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Delete file?",
            Content = $"{row.Name} will be moved to the Recycle Bin. You can restore it from there.",
            PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await SendAsync(new("remove", Id: row.Id, DeleteFile: true));
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = Selected;
        if (selected.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = selected.Count == 1 ? "Remove from list?" : $"Remove {selected.Count} downloads from the list?",
            Content = "Finished files stay where they are. Unfinished downloads stop and their partial files are deleted.",
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await SendAsync(new("remove", Ids: selected.Select(r => r.Id).ToList()));
    }

    private async Task<bool> SendAsync(EngineRequest request)
    {
        try
        {
            var reply = await Shell.Client.SendAsync(request);
            if (!reply.Ok) { ShowError(reply.Error ?? "Something went wrong."); return false; }
            ErrorBar.IsOpen = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        {
            ShowError("The download engine isn't responding. It restarts on its own; try again in a moment.");
            return false;
        }
    }

    private void ShowError(string message) { ErrorBar.Message = message; ErrorBar.IsOpen = true; }

    private void Add_Click(object sender, RoutedEventArgs e) => _ = Shell.OpenAddDialogAsync(null);
    private void ResumeAll_Click(object sender, RoutedEventArgs e) => _ = SendAsync(new("resumeAll"));
    private void PauseAll_Click(object sender, RoutedEventArgs e) => _ = SendAsync(new("pauseAll"));
    private void BulkPause_Click(object sender, RoutedEventArgs e) => _ = SendAsync(new("pause", Ids: Selected.Where(r => r.CanPause).Select(r => r.Id).ToList()));
    private void BulkResume_Click(object sender, RoutedEventArgs e) => _ = SendAsync(new("resume", Ids: Selected.Where(r => r.CanResume).Select(r => r.Id).ToList()));
    private void BulkRemove_Click(object sender, RoutedEventArgs e) => _ = RemoveSelectedAsync();

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<SortMode>(tag, out var mode)) State.Sort = mode;
    }

    private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var selected = Selected;
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space when selected.Count > 0:
                var pause = selected.Any(r => r.CanPause);
                var ids = selected.Where(r => pause ? r.CanPause : r.CanResume).Select(r => r.Id).ToList();
                if (ids.Count > 0) _ = SendAsync(new(pause ? "pause" : "resume", Ids: ids));
                e.Handled = true; break;
            case Windows.System.VirtualKey.Enter when selected.Count == 1:
                if (selected[0].IsCompleted) _ = RunAsync(selected[0], "open");
                e.Handled = true; break;
            case Windows.System.VirtualKey.Delete when selected.Count > 0:
                _ = RemoveSelectedAsync(); e.Handled = true; break;
        }
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is DownloadRow { IsCompleted: true } row) _ = RunAsync(row, "open");
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.WebLink) || e.DataView.Contains(StandardDataFormats.Text) || e.DataView.Contains(StandardDataFormats.Uri))
            e.AcceptedOperation = DataPackageOperation.Link;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        string? text = null;
        if (e.DataView.Contains(StandardDataFormats.WebLink)) text = (await e.DataView.GetWebLinkAsync()).ToString();
        else if (e.DataView.Contains(StandardDataFormats.Text)) text = await e.DataView.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) await Shell.OpenAddDialogAsync(text.Trim());
    }
}
