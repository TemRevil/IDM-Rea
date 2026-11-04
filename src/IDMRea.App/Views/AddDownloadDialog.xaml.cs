using System.Globalization;
using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
namespace IDMRea.App.Views;

/// <summary>Checks a link with the engine (name, size, type, resumable), then adds it now or paused. A web page is
/// asked for its video; when it has one, the person picks a quality instead of a file.</summary>
public sealed partial class AddDownloadDialog : ContentDialog
{
    private static MainWindow Shell => MainWindow.Instance;
    private CancellationTokenSource? probing;
    private ProbeResult? probe;
    private VideoInfo? video;
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
        video = null; QualityBox.ItemsSource = null; QualityBox.Visibility = Visibility.Collapsed; NameBox.IsReadOnly = false;
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
            string? failure = null;
            if (!reply.Ok || reply.Probe is null) failure = reply.Error ?? "Something went wrong.";
            else
            {
                probe = reply.Probe;
                if (!nameEdited) Fill(NameBox, probe.FileName);
                NameBox.IsEnabled = true;
            }
            // A page (or a link the plain check couldn't open) may still play a video.
            if (probe is null || IsPage(probe))
            {
                var tools = await FindVideoAsync(url, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (video != null) failure = null;
                else if (failure == null && tools?.Error is { } error) failure = $"IDM-Rea couldn't set up its video tools: {error} You can still download the page itself.";
            }
            if (failure != null) { ShowError(failure); if (probe is null) InfoLine.Text = ""; }
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

    private static bool IsPage(ProbeResult probe) =>
        probe.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
        || probe.ContentType?.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Asks the engine what video the page plays. The first time, the engine first sets up its video tools;
    /// their progress shows meanwhile. Returns the tools' last state; <see cref="video"/> is set when one was found.</summary>
    private async Task<ToolStatus?> FindVideoAsync(string url, CancellationToken token)
    {
        InfoLine.Text = "Looking for a video on this page...";
        while (true)
        {
            var reply = await Shell.Client.SendAsync(new("probeVideo", Url: url), token);
            if (token.IsCancellationRequested) return null;
            if (reply.Ok && reply.Video is { Choices.Count: > 0 } found)
            {
                video = found;
                QualityBox.ItemsSource = found.Choices.Select(c => c.Label).ToList();
                QualityBox.SelectedIndex = 0;
                QualityBox.Visibility = Visibility.Visible;
                NameBox.IsReadOnly = true; NameBox.IsEnabled = true;
                return reply.Tools;
            }
            if (!reply.Ok || reply.Tools is not { Installing: true } tools) return reply.Tools;
            InfoLine.Text = $"Getting ready for videos. IDM-Rea is downloading its video tools, once. {tools.Percent.ToString(CultureInfo.InvariantCulture)}%";
            await Task.Delay(1500, token);
        }
    }

    private VideoChoice? Chosen() =>
        video is { } v && QualityBox.SelectedIndex is var i && i >= 0 && i < v.Choices.Count ? v.Choices[i] : null;

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Update();

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
        var choice = Chosen();
        if (video != null && choice != null)
        {
            // The engine names a video after its title and quality; the name is shown, not typed.
            Fill(NameBox, Videos.FileName(video.Title, choice));
            InfoLine.Text = $"{(choice.AudioOnly ? "Sound" : "Video")} - {Text.NormalizeDigits(video.Title.Trim())}";
            if (!folderEdited) Fill(FolderBox, Shell.State.Settings.FolderFor(choice.AudioOnly ? Category.Music : Category.Video));
        }
        var name = FileNames.SafeName(Text.NormalizeDigits(NameBox.Text.Trim()));
        if (video == null && probe != null)
        {
            var category = Categories.From(name, probe.ContentType);
            InfoLine.Text = $"{(probe.TotalBytes is long total ? Text.Size(total) : "Size unknown")} - {category} - {(probe.Resumable ? "Can resume" : "Can't resume")}";
            if (!folderEdited) Fill(FolderBox, Shell.State.Settings.FolderFor(category));
        }
        var folder = FolderBox.Text.Trim();
        ExistsLine.Visibility = Visibility.Collapsed;
        var found = video != null ? choice != null : probe != null;
        if (found && NameBox.Text.Trim().Length > 0 && folder.Length > 0)
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
        var ready = found && NameBox.Text.Trim().Length > 0 && folder.Length > 0 && (duplicate == null || duplicateAccepted);
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
            var reply = await Shell.Client.SendAsync(video != null && Chosen() is { } choice
                ? new("addVideo", video.PageUrl, FolderBox.Text.Trim(), StartPaused: startPaused, Choice: choice.Id)
                : new("add", UrlBox.Text.Trim(), FolderBox.Text.Trim(), FileName: NameBox.Text.Trim(), StartPaused: startPaused));
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
