using System.Diagnostics;
using System.Globalization;
using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
namespace IDMRea.App.Views;

/// <summary>Every change is sent to the engine at once; the page then shows what the engine kept (so clamping shows).</summary>
public sealed partial class SettingsPage : Page
{
    private const long KB = 1024, MB = 1024 * 1024;
    private static MainWindow Shell => MainWindow.Instance;
    private readonly Dictionary<Category, TextBox> folders = [];
    private bool loading;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var category in Enum.GetValues<Category>()) FolderRows.Children.Add(FolderRow(category));
        var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
        VersionText.Text = $"Version {version.ToString(3)}";
        Loaded += (_, _) =>
        {
            Shell.State.SettingsChanged += Load;
            Shell.EngineStateChanged += UpdateEngine;
            Shell.IconChanged += ShowIcon;
            Load(Shell.State.Settings);
            UpdateEngine();
            ShowIcon();
        };
        Unloaded += (_, _) => { Shell.State.SettingsChanged -= Load; Shell.EngineStateChanged -= UpdateEngine; Shell.IconChanged -= ShowIcon; };
    }

    private void ShowIcon() { if (Shell.IconPng is { } png) AboutIcon.Source = new BitmapImage(new Uri(png)); }

    private Grid FolderRow(Category category)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = category.ToString(), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(label, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var box = new TextBox { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 };
        AutomationProperties.SetName(box, $"{category} folder");
        box.LostFocus += (_, _) => _ = SaveAsync();
        box.KeyDown += Number_KeyDown;
        var browse = new Button { Content = "Browse" };
        AutomationProperties.SetName(browse, $"Browse for {category} folder");
        browse.Click += async (_, _) =>
        {
            var picked = await new FolderPicker(Shell.AppWindow.Id).PickSingleFolderAsync();
            if (picked?.Path is { Length: > 0 } path) { box.Text = path; await SaveAsync(); }
        };
        Grid.SetColumn(box, 1); Grid.SetColumn(browse, 2);
        row.Children.Add(label); row.Children.Add(box); row.Children.Add(browse);
        folders[category] = box;
        return row;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private bool force;

    /// <summary>A push from the engine leaves a field being typed in alone; the reply to our own save does not.</summary>
    private void SetText(TextBox box, string text) { if ((force || box.FocusState == FocusState.Unfocused) && box.Text != text) box.Text = text; }

    private void Load(EngineSettings s) => Load(s, false);

    private void Load(EngineSettings s, bool force)
    {
        loading = true; this.force = force;
        try
        {
            CloseBox.SelectedIndex = s.OnClose == CloseBehavior.PauseAllAndQuit ? 1 : 0;
            StartupSwitch.IsOn = s.StartWithWindows;
            DoneSwitch.IsOn = s.NotifyOnComplete;
            FailSwitch.IsOn = s.NotifyOnFailure;
            foreach (var (category, box) in folders) SetText(box, s.FolderFor(category));
            SetText(AtOnceBox, Number(s.DownloadsAtOnce));
            SetText(PartsBox, Number(s.PartsPerFile));
            PartsBox.Visibility = s.UltraPerformance ? Visibility.Collapsed : Visibility.Visible;
            PartsLocked.Visibility = s.UltraPerformance ? Visibility.Visible : Visibility.Collapsed;
            PartsLocked.Text = Number(s.EffectiveParts);
            AutomationProperties.SetName(PartsLocked, $"Parts per file: {Number(s.EffectiveParts)}");
            PartsHint.Text = s.UltraPerformance ? "Set by Ultra performance" : "1 to 16. More parts can be faster on servers that allow it.";
            var limit = s.SpeedLimitBytesPerSecond;
            LimitSwitch.IsOn = limit > 0;
            if (limit > 0)
            {
                var useMb = limit >= MB && limit % MB == 0;
                LimitUnit.SelectedIndex = useMb ? 1 : 0;
                SetText(LimitBox, Number(useMb ? limit / MB : Math.Max(1, limit / KB)));
            }
            else
            {
                if (LimitUnit.SelectedIndex < 0) LimitUnit.SelectedIndex = 1;
                if (LimitBox.Text.Length == 0) SetText(LimitBox, "5");
            }
            LimitBox.IsEnabled = LimitUnit.IsEnabled = limit > 0;
            SetText(RetryBox, Number(s.RetryAttempts));
            UltraSwitch.IsOn = s.UltraPerformance;
            BrowserSwitch.IsOn = s.BrowserTakeover;
            SetText(BrowserMinBox, Number(s.BrowserMinBytes / MB));
            ThemeBox.SelectedIndex = (int)s.Theme;
        }
        finally { loading = false; }
    }

    private static int? Parse(TextBox box) =>
        long.TryParse(Text.NormalizeDigits(box.Text).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : null;

    private EngineSettings Gather()
    {
        var current = Shell.State.Settings;
        var unit = LimitUnit.SelectedIndex == 0 ? KB : MB;
        var limitValue = Parse(LimitBox) ?? 0;
        var chosen = folders.Where(f => f.Value.Text.Trim().Length > 0)
            .ToDictionary(f => f.Key, f => f.Value.Text.Trim());
        return current with
        {
            OnClose = CloseBox.SelectedIndex == 1 ? CloseBehavior.PauseAllAndQuit : CloseBehavior.KeepInTray,
            StartWithWindows = StartupSwitch.IsOn,
            NotifyOnComplete = DoneSwitch.IsOn,
            NotifyOnFailure = FailSwitch.IsOn,
            Folders = chosen,
            DownloadsAtOnce = Parse(AtOnceBox) ?? current.DownloadsAtOnce,
            PartsPerFile = Parse(PartsBox) ?? current.PartsPerFile,
            SpeedLimitBytesPerSecond = LimitSwitch.IsOn ? Math.Max(1, limitValue) * unit : 0,
            RetryAttempts = Parse(RetryBox) ?? current.RetryAttempts,
            UltraPerformance = UltraSwitch.IsOn,
            BrowserTakeover = BrowserSwitch.IsOn,
            BrowserMinBytes = Parse(BrowserMinBox) is int minMb ? Math.Max(0, minMb) * MB : current.BrowserMinBytes,
            Theme = ThemeBox.SelectedIndex switch { 1 => AppTheme.Light, 2 => AppTheme.Dark, _ => AppTheme.System },
        };
    }

    private async Task SaveAsync()
    {
        if (loading || !IsLoaded) return;
        var next = Gather();
        try
        {
            var reply = await Shell.Client.SendAsync(new("setSettings", Settings: next));
            if (!reply.Ok) { ShowError(reply.Error ?? "The settings could not be saved."); Load(Shell.State.Settings); return; }
            ErrorBar.IsOpen = false;
            if (reply.Settings is { } kept) { Shell.ApplyTheme(kept.Theme, remember: true); Load(kept, force: true); }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            ShowError("The download engine isn't running, so settings can't be saved right now.");
        }
    }

    private void ShowError(string message) { ErrorBar.Message = message; ErrorBar.IsOpen = true; }

    private void Setting_Changed(object sender, RoutedEventArgs e) => _ = SaveAsync();
    private void Number_LostFocus(object sender, RoutedEventArgs e) => _ = SaveAsync();

    private void Number_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true; _ = SaveAsync();
    }

    private void UpdateEngine()
    {
        var on = Shell.EngineConnected;
        EngineText.Text = on ? "Download engine running" : "Download engine stopped";
        EngineButton.Content = on ? "Stop engine" : "Start engine";
        AutomationProperties.SetName(EngineButton, on ? "Stop engine" : "Start engine");
    }

    private async void Engine_Click(object sender, RoutedEventArgs e)
    {
        EngineButton.IsEnabled = false;
        try
        {
            if (Shell.EngineConnected)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Stop the download engine?", RequestedTheme = ActualTheme,
                    Content = "Downloads stop now and carry on by themselves the next time the engine starts.",
                    PrimaryButtonText = "Stop engine", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Shell.Client.SendAsync(new("shutdown"));
            }
            else await Shell.Client.EnsureStartedAsync();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or FileNotFoundException) { ShowError(ex.Message); }
        finally { EngineButton.IsEnabled = true; }
    }

    /// <summary>
    /// Registers the browser helper and copies the extension into the data folder (the engine does both), then walks the
    /// person through the browser's own steps: it opens each installed browser's extensions page on request and puts the
    /// folder path on the clipboard for Load unpacked. Browsers allow nothing more from outside the store.
    /// </summary>
    private async void AddBrowser_Click(object sender, RoutedEventArgs e)
    {
        AddBrowserButton.IsEnabled = false;
        try
        {
            var reply = await Shell.Client.SendAsync(new("registerBrowser"));
            if (!reply.Ok) { ShowError(reply.Error ?? "IDM-Rea could not connect to the browser."); return; }
            ErrorBar.IsOpen = false;
            var folder = Protocol.ExtensionFolderFor(Protocol.DataDirectory);
            CopyText(folder);
            await ShowBrowserSteps(folder);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            ShowError("The download engine isn't running, so the browser can't be connected right now.");
        }
        finally { AddBrowserButton.IsEnabled = true; }
    }

    private async Task ShowBrowserSteps(string folder)
    {
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        var steps = new StackPanel { Spacing = 12, MaxWidth = 460 };

        var browsers = InstalledBrowsers.All();
        steps.Children.Add(Line(browsers.Count > 0 ? "1. Open the extensions page:" : "1. In your browser, open the extensions page: chrome://extensions in Chrome, edge://extensions in Edge."));
        if (browsers.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var browser in browsers)
            {
                var open = new Button { Content = $"Open {browser.Name}" };
                AutomationProperties.SetName(open, $"Open the {browser.Name} extensions page");
                open.Click += (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(browser.ExePath, browser.ExtensionsPage) { UseShellExecute = false })?.Dispose(); }
                    catch (System.ComponentModel.Win32Exception) { CopyText(browser.ExtensionsPage); }
                };
                row.Children.Add(open);
            }
            steps.Children.Add(row);
        }
        steps.Children.Add(Line("2. Turn on Developer mode."));
        steps.Children.Add(Line("3. Press Load unpacked, paste the folder path into the Folder box, and press Select Folder. The path is already copied:"));
        var path = new TextBox { Text = folder, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(path, "Extension folder");
        steps.Children.Add(path);
        steps.Children.Add(new TextBlock
        {
            Text = "The extension's menu then says Connected. IDM-Rea keeps this folder up to date when it is updated, so this is needed only once.",
            TextWrapping = TextWrapping.Wrap, Foreground = secondary,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "Add IDM-Rea to your browser", RequestedTheme = ActualTheme, Content = steps,
            PrimaryButtonText = "Copy path again", CloseButtonText = "Done", DefaultButton = ContentDialogButton.Close,
        };
        dialog.PrimaryButtonClick += (_, args) => { args.Cancel = true; CopyText(folder); };
        await dialog.ShowAsync();
    }

    private static void CopyText(string text)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Protocol.DataDirectory);
        Process.Start("explorer.exe", $"\"{Protocol.DataDirectory}\"");
    }
}
