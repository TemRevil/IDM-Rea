using System.Diagnostics;
using System.Security;
using IDMRea.Contracts;
using Microsoft.Win32;
using Windows.UI.Notifications;
namespace IDMRea.Engine.Windows;

/// <summary>Windows notifications for finished and failed downloads, under the app's own name and icon.</summary>
public sealed class Toasts
{
    public const string AppId = "IDMRea";
    private readonly Func<Guid, DownloadJob?> find;
    private readonly ToastNotifier notifier;

    public Toasts(Func<Guid, DownloadJob?> find)
    {
        this.find = find;
        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId, writable: true))
        {
            key.SetValue("DisplayName", "IDM-Rea");
            var icon = Path.Combine(AppLauncher.AppFolder, "AppIcon.png");
            if (File.Exists(icon)) key.SetValue("IconUri", icon); else key.DeleteValue("IconUri", false);
        }
        Native.SetCurrentProcessExplicitAppUserModelID(AppId);
        notifier = ToastNotificationManager.CreateToastNotifier(AppId);
    }

    /// <summary>The toast XML. Clicking the toast opens the job's details; buttons carry <c>action|job id</c>.</summary>
    public static string BuildXml(DownloadJob job)
    {
        var id = job.Id.ToString();
        var finished = job.Status == DownloadStatus.Completed;
        var line = finished ? "Download finished" : Text.Describe(job.ErrorKind == ErrorKind.None ? ErrorKind.Other : job.ErrorKind, job.HttpStatus);
        var actions = finished
            ? $"<action content=\"Open\" arguments=\"open|{id}\"/><action content=\"Show in folder\" arguments=\"folder|{id}\"/>"
            : $"<action content=\"Details\" arguments=\"details|{id}\"/>";
        return $"<toast launch=\"details|{id}\"><visual><binding template=\"ToastGeneric\"><text>{SecurityElement.Escape(job.FileName)}</text>" +
               $"<text>{SecurityElement.Escape(line)}</text></binding></visual><actions>{actions}</actions></toast>";
    }

    public void Show(DownloadJob job)
    {
        var xml = new global::Windows.Data.Xml.Dom.XmlDocument(); xml.LoadXml(BuildXml(job));
        var toast = new ToastNotification(xml) { Tag = job.Id.ToString("N")[..16] };
        toast.Activated += (_, args) => { if (args is ToastActivatedEventArgs activated) Activate(activated.Arguments); };
        notifier.Show(toast);
    }

    private void Activate(string arguments)
    {
        var parts = arguments.Split('|');
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var id)) return;
        var job = find(id);
        try
        {
            switch (parts[0])
            {
                case "open" when job != null && File.Exists(job.Destination):
                    Process.Start(new ProcessStartInfo(job.Destination) { UseShellExecute = true })?.Dispose(); break;
                case "folder" when job != null:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.Destination}\"") { UseShellExecute = false })?.Dispose(); break;
                default: AppLauncher.Open($"--job {id}"); break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { AppLauncher.Open($"--job {id}"); }
    }
}
