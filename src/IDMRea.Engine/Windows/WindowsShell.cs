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

    /// <summary>Starts the tray and notifications once the manager exists. Quit pauses everything as the person's choice, then stops.</summary>
    public void Attach(DownloadManager downloads, CancellationTokenSource stopEngine)
    {
        manager = downloads; stop = stopEngine;
        try { toasts = new Toasts(id => downloads.Snapshot().FirstOrDefault(j => j.Id == id)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or IOException) { toasts = null; }
        tray = new TrayIcon(OnCommand);
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

    public bool Recycle(string path) => RecycleBin.Send(path);

    private void OnCommand(TrayCommand command)
    {
        if (manager == null) return;
        switch (command)
        {
            case TrayCommand.Open: AppLauncher.Open(null); break;
            case TrayCommand.AddFromClipboard: AppLauncher.Open("--add-clipboard"); break;
            case TrayCommand.PauseAll: _ = manager.PauseAllAsync(PausedBy.Person); break;
            case TrayCommand.ResumeAll: _ = manager.ResumeAllAsync(); break;
            case TrayCommand.Quit: _ = QuitAsync(); break;
        }
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
