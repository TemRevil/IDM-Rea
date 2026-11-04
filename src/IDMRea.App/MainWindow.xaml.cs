using System.Runtime.InteropServices;
using IDMRea.App.Views;
using IDMRea.Contracts;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace IDMRea.App;

public sealed partial class MainWindow : Window
{
    private static readonly string ThemeFile = Path.Combine(Protocol.DataDirectory, "app-theme.txt");
    private readonly EngineSubscription subscription = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool subscribed, quitting;
    private LaunchArguments? pending;

    public static MainWindow Instance { get; private set; } = null!;
    public AppState State { get; } = new();
    public EngineClient Client { get; } = new();
    public bool EngineConnected => subscription.Connected;
    public event Action? EngineStateChanged;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        ApplyTheme(ReadCachedTheme());
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        AppWindow.SetIcon(Path.Combine(assets, "AppIcon.ico"));
        TitleIcon.ImageSource = new BitmapImage(new Uri(Path.Combine(assets, "AppIcon.png")));
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        else Root.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.PreferredMinimumWidth = 720; presenter.PreferredMinimumHeight = 480; }

        State.PropertyChanged += (_, _) => UpdateShell();
        State.SettingsChanged += s => ApplyTheme(s.Theme, remember: true);
        subscription.Received += update => DispatcherQueue.TryEnqueue(() => State.Apply(update));
        subscription.ConnectionChanged += _ => DispatcherQueue.TryEnqueue(UpdateEngineState);
        ticker.Tick += (_, _) => { foreach (var row in State.All) if (row.Status == DownloadStatus.Retrying) row.Refresh(); };
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) => { lifetime.Cancel(); subscription.Stop(); };

        ContentFrame.Navigate(typeof(DownloadsPage));
        UpdateShell();
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        try { await Client.EnsureStartedAsync(); }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or OperationCanceledException or TimeoutException)
        {
            EngineText.Text = "Engine unavailable: " + ex.Message;
        }
        StartSubscription();
    }

    private void StartSubscription()
    {
        if (subscribed || lifetime.IsCancellationRequested) return;
        subscribed = true;
        _ = subscription.StartAsync(lifetime.Token);
    }

    private void StopSubscription() { subscribed = false; subscription.Stop(); }

    /// <summary>Nothing is pushed to a minimised window; the full list comes again on restore.</summary>
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange && !args.DidSizeChange || sender.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Minimized) StopSubscription(); else StartSubscription();
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (quitting || State.Settings.OnClose != CloseBehavior.PauseAllAndQuit || !EngineConnected) return;
        args.Cancel = true; quitting = true;
        try
        {
            await Client.SendAsync(new("pauseAll"));
            await Client.SendAsync(new("shutdown"));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
        Close();
    }

    private void UpdateShell()
    {
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            var count = item.Tag is string tag ? State.Counts.GetValueOrDefault(tag) : 0;
            item.InfoBadge ??= (string)item.Tag == DownloadViews.Failed
                ? new InfoBadge { Style = (Style)Application.Current.Resources["CriticalValueInfoBadgeStyle"] }
                : new InfoBadge();
            item.InfoBadge.Value = count;
            item.InfoBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var totals = State.Totals;
        StatusText.Text = totals.Active > 0 ? $"{totals.Active} downloading - {Text.Speed(totals.BytesPerSecond)}" : "Ready";
        if (State.All.Any(r => r.Status == DownloadStatus.Retrying)) ticker.Start(); else ticker.Stop();
        if (pending is { } args && State.HasSettings) { pending = null; Handle(args); }
    }

    private void UpdateEngineState()
    {
        var on = subscription.Connected;
        EngineText.Text = on ? "Engine running" : "Connecting to engine";
        EngineDot.Fill = on ? new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]) : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        EngineStateChanged?.Invoke();
    }

    public void ApplyTheme(AppTheme theme, bool remember = false)
    {
        Root.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        var dark = Root.ActualTheme == ElementTheme.Dark || theme == AppTheme.Dark
            || (theme == AppTheme.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Windows.UI.Color.FromArgb(0xFF, 0x70, 0x70, 0x70);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
        if (!remember) return;
        try { if (ReadCachedTheme() != theme) { Directory.CreateDirectory(Protocol.DataDirectory); File.WriteAllText(ThemeFile, theme.ToString()); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static AppTheme ReadCachedTheme()
    {
        try { return File.Exists(ThemeFile) && Enum.TryParse<AppTheme>(File.ReadAllText(ThemeFile).Trim(), out var theme) ? theme : AppTheme.System; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return AppTheme.System; }
    }

    /// <summary>A second launch: bring this window forward, then do what it asked.</summary>
    public void Reactivate(LaunchArguments args)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Handle(args);
    }

    public void Handle(LaunchArguments args)
    {
        if (args.Job is null && !args.AddFromClipboard) return;
        if (!State.HasSettings) { pending = args; return; }
        if (args.Job is Guid id) ShowJob(id);
    }

    public void ShowJob(Guid id)
    {
        SelectNav(DownloadViews.All);
        (ContentFrame.Content as DownloadsPage)?.Select(id);
    }

    private void SelectNav(string tag)
    {
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item != null) Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        if (tag == "settings") return;
        State.Filter = tag;
        if (ContentFrame.Content is not DownloadsPage) ContentFrame.Navigate(typeof(DownloadsPage));
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) State.Search = sender.Text;
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard); args.Handled = true;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
