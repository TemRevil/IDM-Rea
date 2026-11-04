using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
namespace IDMRea.App.Views;

/// <summary>Checks a link with the engine (name, size, type, resumable), then adds it now or paused.</summary>
public sealed partial class AddDownloadDialog : ContentDialog
{
    private static MainWindow Shell => MainWindow.Instance;
    private CancellationTokenSource? probing;
    private ProbeResult? probe;
    private bool nameEdited, folderEdited, filling, duplicateAccepted;
    private Guid? duplicate;

    /// <summary>Set when the person chose "Open existing" for a link already in the list.</summary>
    public Guid? OpenExisting { get; private set; }

    public AddDownloadDialog(string? url)
    {
        InitializeComponent();
        Fill(FolderBox, Shell.State.Settings.FolderFor(Category.Other));
        if (!string.IsNullOrWhiteSpace(url)) UrlBox.Text = url.Trim();
        Opened += (_, _) => { UrlBox.Focus(FocusState.Programmatic); UrlBox.SelectAll(); if (UrlBox.Text.Length > 0) UrlChanged(); };
        Closed += (_, _) => probing?.Cancel();
    }

    public static bool IsLink(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void Fill(TextBox box, string text) { filling = true; box.Text = text; filling = false; }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) => UrlChanged();

    private async void UrlChanged()
    {
        probing?.Cancel();
        var cts = probing = new CancellationTokenSource();
        probe = null; duplicateAccepted = false; ErrorLine.Visibility = Visibility.Collapsed;
        var url = UrlBox.Text.Trim();
        CheckDuplicate(url);
        Update();
        if (url.Length == 0) { InfoLine.Text = "Paste or type a link to see the file's details."; return; }
        if (!IsLink(url)) { InfoLine.Text = "Enter a link that starts with http:// or https://."; return; }
        try
        {
            await Task.Delay(400, cts.Token);
            Probing.Visibility = Visibility.Visible; Probing.IsActive = true;
            InfoLine.Text = "Checking the link...";
            var reply = await Shell.Client.SendAsync(new("probe", Url: url), cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!reply.Ok || reply.Probe is null) { ShowError(reply.Error ?? "Something went wrong."); InfoLine.Text = ""; }
            else
            {
                probe = reply.Probe;
                if (!nameEdited) Fill(NameBox, probe.FileName);
                NameBox.IsEnabled = true;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            ShowError("The download engine isn't responding. Try again in a moment."); InfoLine.Text = "";
        }
        finally
        {
            if (ReferenceEquals(probing, cts)) { Probing.IsActive = false; Probing.Visibility = Visibility.Collapsed; }
        }
        Update();
    }

    private void CheckDuplicate(string url)
    {
        duplicate = Shell.State.All.FirstOrDefault(r => string.Equals(r.Job.Url.Trim(), url, StringComparison.OrdinalIgnoreCase))?.Id;
        DuplicateBar.IsOpen = duplicate != null && !duplicateAccepted;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) { if (!filling) nameEdited = true; Update(); }
    private void FolderBox_TextChanged(object sender, TextChangedEventArgs e) { if (!filling) folderEdited = true; Update(); }

    /// <summary>Category, size and resumable line; the folder for that category; the "(1)" name if the file exists.</summary>
    private void Update()
    {
        var name = FileNames.SafeName(Text.NormalizeDigits(NameBox.Text.Trim()));
        if (probe != null)
        {
            var category = Categories.From(name, probe.ContentType);
            InfoLine.Text = $"{(probe.TotalBytes is long total ? Text.Size(total) : "Size unknown")} - {category} - {(probe.Resumable ? "Can resume" : "Can't resume")}";
            if (!folderEdited) Fill(FolderBox, Shell.State.Settings.FolderFor(category));
        }
        var folder = FolderBox.Text.Trim();
        ExistsLine.Visibility = Visibility.Collapsed;
        if (probe != null && NameBox.Text.Trim().Length > 0 && folder.Length > 0)
        {
            var taken = Shell.State.All.Select(r => r.Job.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                var unique = FileNames.UniquePath(folder, name, p => File.Exists(p) || taken.Contains(p));
                if (!string.Equals(Path.GetFileName(unique), name, StringComparison.OrdinalIgnoreCase))
                {
                    ExistsLine.Text = $"A file with this name exists. It will be saved as {Path.GetFileName(unique)}.";
                    ExistsLine.Visibility = Visibility.Visible;
                }
            }
            catch (ArgumentException) { }
        }
        var ready = probe != null && NameBox.Text.Trim().Length > 0 && folder.Length > 0 && (duplicate == null || duplicateAccepted);
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = ready;
    }

    private void ShowError(string message) { ErrorLine.Text = message; ErrorLine.Visibility = Visibility.Visible; }

    private void OpenExisting_Click(object sender, RoutedEventArgs e) { OpenExisting = duplicate; Hide(); }
    private void DownloadAgain_Click(object sender, RoutedEventArgs e) { duplicateAccepted = true; DuplicateBar.IsOpen = false; Update(); }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(Shell.AppWindow.Id);
        var picked = await picker.PickSingleFolderAsync();
        if (picked?.Path is { Length: > 0 } path) { FolderBox.Text = path; folderEdited = true; }
    }

    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args) => await AddAsync(args, startPaused: false);
    private async void Secondary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args) => await AddAsync(args, startPaused: true);

    private async Task AddAsync(ContentDialogButtonClickEventArgs args, bool startPaused)
    {
        var deferral = args.GetDeferral();
        try
        {
            IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = false;
            var reply = await Shell.Client.SendAsync(new("add", UrlBox.Text.Trim(), FolderBox.Text.Trim(), FileName: NameBox.Text.Trim(), StartPaused: startPaused));
            if (!reply.Ok) { ShowError(reply.Error ?? "Something went wrong."); args.Cancel = true; }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            ShowError("The download engine isn't responding. Try again in a moment."); args.Cancel = true;
        }
        finally
        {
            if (args.Cancel) Update();
            deferral.Complete();
        }
    }
}
