using IDMRea.Contracts;
using IDMRea.Core;
namespace IDMRea.Engine.Windows;

/// <summary>Everything the engine does on Windows besides downloading: tray, notifications, start with Windows, Recycle Bin, memory trim.</summary>
public sealed class WindowsShell : IEngineShell, IDisposable
{
    private readonly StartupRegistration startup = new();
    private readonly MemoryTrim trim = new();
    private readonly object gate = new();
    private DownloadManager? manager;
    private CancellationTokenSource? stop;
    private TrayIcon? tray;
    private Toasts? toasts;
    private Timer? tooltipTimer;
    private EngineSettings settings = new();
    private global::Windows.UI.ViewManagement.UISettings? colors; // kept alive: it reports accent changes
    private Rgb? ring;

    /// <summary>Starts the tray and notifications once the manager exists. Quit pauses everything as the person's choice, then stops.</summary>
    public void Attach(DownloadManager downloads, CancellationTokenSource stopEngine)
    {
        manager = downloads; stop = stopEngine;
        try { toasts = new Toasts(id => downloads.Snapshot().FirstOrDefault(j => j.Id == id)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or IOException) { toasts = null; }
        tray = new TrayIcon(OnCommand, MenuState);
        FollowAccent();
        SettingsChanged(downloads.Settings);
        downloads.JobChanged += _ => Activity();
        Activity();
    }

    public void Notify(DownloadJob job)
    {
        var wanted = job.Status == DownloadStatus.Completed ? settings.NotifyOnComplete : job.Status == DownloadStatus.Failed && settings.NotifyOnFailure;
        if (!wanted || toasts == null) return;
        try { toasts.Show(job); } catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException) { }
    }

    public void SettingsChanged(EngineSettings next)
    {
        settings = next;
        try { if (Environment.ProcessPath is { } exe) startup.Set(next.StartWithWindows, exe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
    }

    /// <summary>The tray icon and the notifications' picture are drawn in the Windows accent colour, and redrawn when it changes.</summary>
    private void FollowAccent()
    {
        try
        {
            colors = new global::Windows.UI.ViewManagement.UISettings();
            colors.ColorValuesChanged += (_, _) => UpdateIcon();
            UpdateIcon();
        }
        catch (System.Runtime.InteropServices.COMException) { colors = null; }
    }

    private void UpdateIcon()
    {
        if (colors == null) return;
        static Rgb Of(global::Windows.UI.Color c) => new(c.R, c.G, c.B);
        var next = IconArt.RingFor(Of(colors.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent)),
            Of(colors.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight2)));
        lock (gate) { if (next == ring) return; ring = next; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var (ico, png) = IconArt.Files(next);
                // Two quick accent changes draw in parallel: only the newest colour may reach the tray.
                lock (gate)
                {
                    if (ring != next) return;
                    tray?.SetIcon(ico);
                    if (toasts != null) Toasts.SetIcon(png);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }

    public bool Recycle(string path) => RecycleBin.Send(path);

    private TrayState MenuState()
    {
        var jobs = manager!.Snapshot(); var totals = manager.Totals();
        return new(totals.Active, totals.BytesPerSecond,
            CanPause: jobs.Any(j => j.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Retrying),
            CanResume: jobs.Any(j => j.Status == DownloadStatus.Paused),
            SpeedLimit: manager.Settings.SpeedLimitBytesPerSecond);
    }

    private void OnCommand(TrayChoice choice)
    {
        if (manager == null) return;
        switch (choice.Command)
        {
            case TrayCommand.Open: AppLauncher.Open(null); break;
            case TrayCommand.AddFromClipboard: AppLauncher.Open("--add-clipboard"); break;
            case TrayCommand.PauseAll: _ = manager.PauseAllAsync(PausedBy.Person); break;
            case TrayCommand.ResumeAll: _ = manager.ResumeAllAsync(); break;
            case TrayCommand.SpeedLimit: _ = manager.ApplySettingsAsync(manager.Settings with { SpeedLimitBytesPerSecond = choice.Value }); break;
            case TrayCommand.OpenFolder: OpenFolder(manager.Settings.FolderFor(Category.Other)); break;
            case TrayCommand.Settings: AppLauncher.Open("--settings"); break;
            case TrayCommand.Quit: _ = QuitAsync(); break;
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    private async Task QuitAsync()
    {
        try { await manager!.PauseAllAsync(PausedBy.Person); }
        finally { stop!.Cancel(); }
    }

    /// <summary>While anything downloads, the tooltip refreshes once a second; when all is quiet, no timer runs.</summary>
    private void Activity()
    {
        if (manager == null) return;
        var active = manager.Totals().Active > 0;
        trim.Update(active);
        lock (gate)
        {
            if (active && tooltipTimer == null) tooltipTimer = new Timer(_ => RefreshTooltip(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            else if (!active && tooltipTimer != null) { tooltipTimer.Dispose(); tooltipTimer = null; }
        }
        if (!active) tray?.SetTooltip("IDM-Rea - idle");
    }

    private void RefreshTooltip()
    {
        if (manager == null) return;
        var totals = manager.Totals();
        tray?.SetTooltip(totals.Active == 0 ? "IDM-Rea - idle" : $"{totals.Active} downloading - {Text.Speed(totals.BytesPerSecond)}");
    }

    public void Dispose()
    {
        lock (gate) { tooltipTimer?.Dispose(); tooltipTimer = null; }
        tray?.Dispose(); trim.Dispose();
    }
}
