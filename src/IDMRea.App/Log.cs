using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using IDMRea.Contracts;
using Microsoft.UI.Dispatching;
namespace IDMRea.App;

/// <summary>
/// The app's log, Logs\app.log in the data folder: crashes, errors, moments the window stopped responding and engine
/// calls that were slow or failed, plus the crashes Windows itself recorded for IDM-Rea. It stays on this PC;
/// Settings > About > Logs packs it with the engine's and the browser link's logs to send.
/// </summary>
public static class Log
{
    public static readonly LogFile File = new(Protocol.LogsDirectory, "app");

    /// <summary>The page on screen, so a freeze or an error says where it happened.</summary>
    public static volatile string Where = "";

    /// <summary>Where something happened, as words: "on the settings page", or "while starting".</summary>
    public static string Place => Where.Length == 0 ? "while starting" : $"on the {Where} page";

    /// <summary>The window not answering for this long counts as a freeze.</summary>
    public static readonly TimeSpan Freeze = TimeSpan.FromMilliseconds(500);

    public static string Version => typeof(Log).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static void Info(string source, string message) => File.Info(source, message);
    public static void Warn(string source, string message, Exception? error = null) => File.Warn(source, message, error);
    public static void Error(string source, string message, Exception? error = null) => File.Error(source, message, error);
    public static void Crash(string source, string message, Exception? error = null) => File.Crash(source, message, error);

    /// <summary>Starts logging: what's running, every error nobody caught, and a watch on the window's responsiveness.</summary>
    public static void Start(DispatcherQueue ui)
    {
        Info("App", $"Starting IDM-Rea {Version} on {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("App", $"IDM-Rea closed on an error it didn't expect ({Place})", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Error("App", "A background task failed and nothing was waiting for it", e.Exception);
        new Thread(() => Watch(ui)) { IsBackground = true, Name = "Freeze watch", Priority = ThreadPriority.AboveNormal }.Start();
        _ = Task.Run(ImportWindowsReports);
    }

    /// <summary>
    /// Posts a no-op to the window's thread every tenth of a second and times how long it waits. A wait past
    /// <see cref="Freeze"/> is logged when the window answers; one past five seconds is logged while it still hasn't,
    /// so a hang that ends with Windows closing the app is on record too. Time the PC spent asleep doesn't count.
    /// </summary>
    private static void Watch(DispatcherQueue ui)
    {
        long ping = 0, answered = 0, sentAt = 0;
        var reportedHang = false;
        var waitingAt = ""; // where the window was when the unanswered ping was sent
        var last = Stopwatch.GetTimestamp();
        while (true)
        {
            Thread.Sleep(100);
            var now = Stopwatch.GetTimestamp();
            var slept = Stopwatch.GetElapsedTime(last, now) > TimeSpan.FromSeconds(2);
            last = now;
            if (slept) { answered = Interlocked.Read(ref ping); reportedHang = false; continue; } // the PC was asleep, not the window

            if (Interlocked.Read(ref answered) == ping)
            {
                var id = ++ping;
                sentAt = now;
                var sent = now;
                var place = waitingAt = Place;
                if (!ui.TryEnqueue(() =>
                    {
                        // A ping left over from before the PC slept is dropped: its wait includes the sleep.
                        if (Interlocked.Read(ref answered) >= id) return;
                        var waited = Stopwatch.GetElapsedTime(sent);
                        Interlocked.Exchange(ref answered, id);
                        if (waited >= Freeze) File.Write(LogLevel.Freeze, "Window", $"didn't respond for {LogFile.Seconds(waited)} ({place})");
                    })) return; // the window's thread has ended
                reportedHang = false;
            }
            else if (!reportedHang && Stopwatch.GetElapsedTime(sentAt, now) > TimeSpan.FromSeconds(5))
            {
                reportedHang = true;
                File.Write(LogLevel.Freeze, "Window", $"hasn't responded for 5 s and counting ({waitingAt})");
            }
        }
    }

    private static readonly string CheckedFile = Path.Combine(File.Directory, "windows-reports-checked.txt");

    /// <summary>
    /// Copies in what Windows recorded about IDM-Rea since the last look (a week back the first time): the app, the
    /// engine or the browser link crashing, and the app hanging until it was closed. Those happen where these logs
    /// can't write.
    /// </summary>
    private static void ImportWindowsReports()
    {
        try
        {
            var since = DateTime.UtcNow.AddDays(-7);
            if (System.IO.File.Exists(CheckedFile) && DateTime.TryParse(System.IO.File.ReadAllText(CheckedFile).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) since = t;
            var checkedAt = DateTime.UtcNow;
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[Provider[@Name='Application Error' or @Name='Application Hang' or @Name='.NET Runtime'] and (EventID=1000 or EventID=1002 or EventID=1026)" +
                $" and TimeCreated[@SystemTime>'{since:yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                {
                    var text = e.FormatDescription() ?? string.Join("\n", e.Properties.Select(p => p.Value));
                    if (CrashReports.PartOf(text) is not { } part) continue;
                    var when = e.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "?";
                    var what = e.Id == 1002 ? "stopped responding and was closed" : "crashed";
                    File.Write(LogLevel.Crash, "Windows", $"recorded at {when} that the IDM-Rea {part} {what} ({e.ProviderName} {e.Id}):\n{text}");
                }
            Directory.CreateDirectory(File.Directory);
            System.IO.File.WriteAllText(CheckedFile, checkedAt.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) { Warn("App", "Couldn't read Windows' crash reports", ex); }
    }

    /// <summary>Everything to send in one .zip: the app's, the engine's and the browser link's logs, and what was running.</summary>
    public static void Bundle(Stream output, bool engineRunning)
    {
        var about = string.Join("\n",
            $"IDM-Rea {Version}",
            $"Download engine {(engineRunning ? "running" : "not running")}",
            $"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})",
            $".NET {Environment.Version}",
            $"Time zone {TimeZoneInfo.Local.Id}",
            $"Made {DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}", "");
        LogBundle.Write(output, [("logs", File.Directory)], about);
    }
}
