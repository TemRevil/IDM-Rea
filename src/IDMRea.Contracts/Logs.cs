using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
namespace IDMRea.Contracts;

/// <summary>How serious a log line is. Slow and Freeze are for things that worked but kept you waiting.</summary>
public enum LogLevel { Info, Slow, Freeze, Warning, Error, Crash }

/// <summary>
/// A plain-text log that stays small: one line per event (more when there's an exception), and when the file passes
/// <paramref name="maxBytes"/> it becomes "name.1.log", the older ones move up, and the oldest past
/// <paramref name="keep"/> is deleted. Safe to write from any thread; other programs can read it while it's open.
/// </summary>
public sealed class LogFile(string directory, string name, long maxBytes = 1_000_000, int keep = 4)
{
    private readonly Lock gate = new();

    public string Directory => directory;
    public string Path => System.IO.Path.Combine(directory, name + ".log");

    public void Info(string source, string message) => Write(LogLevel.Info, source, message);
    public void Warn(string source, string message, Exception? error = null) => Write(LogLevel.Warning, source, message, error);
    public void Error(string source, string message, Exception? error = null) => Write(LogLevel.Error, source, message, error);
    public void Crash(string source, string message, Exception? error = null) => Write(LogLevel.Crash, source, message, error);

    public void Write(LogLevel level, string source, string message, Exception? error = null)
    {
        var line = Format(DateTimeOffset.Now, level, source, message, error);
        lock (gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                if (File.Exists(Path) && new FileInfo(Path).Length + line.Length > maxBytes) Rotate();
                using var file = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                file.Write(Encoding.UTF8.GetBytes(line));
            }
            catch (IOException) { /* a full or locked disk mustn't take the program down with it */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>"2026-11-05 19:42:01.123 +03:00 SLOW    Engine: list took 1.6 s", then the exception, indented.</summary>
    public static string Format(DateTimeOffset time, LogLevel level, string source, string message, Exception? error)
    {
        var b = new StringBuilder();
        b.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(8)).Append(source).Append(": ")
            .Append(message.ReplaceLineEndings(" | "));
        b.Append('\n');
        if (error is not null)
            foreach (var l in error.ToString().ReplaceLineEndings("\n").Split('\n'))
                if (l.Length > 0) b.Append("    ").Append(l).Append('\n');
        return b.ToString();
    }

    /// <summary>"1.6 s".</summary>
    public static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>This log's files, newest first.</summary>
    public IEnumerable<string> Files() =>
        Enumerable.Range(0, keep + 1).Select(i => i == 0 ? Path : System.IO.Path.Combine(directory, $"{name}.{i}.log")).Where(File.Exists);

    private void Rotate()
    {
        var oldest = System.IO.Path.Combine(directory, $"{name}.{keep}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = keep - 1; i >= 1; i--)
        {
            var from = System.IO.Path.Combine(directory, $"{name}.{i}.log");
            if (File.Exists(from)) File.Move(from, System.IO.Path.Combine(directory, $"{name}.{i + 1}.log"));
        }
        File.Move(Path, System.IO.Path.Combine(directory, $"{name}.1.log"));
    }
}

/// <summary>The logs packed into one .zip to send: every .log file of each folder given, and a note about the PC.</summary>
public static class LogBundle
{
    /// <param name="folders">Each folder of logs, and the folder name it gets inside the zip.</param>
    /// <param name="about">What goes in about.txt: versions, Windows, when it was made.</param>
    public static void Write(Stream output, IEnumerable<(string Name, string Directory)> folders, string about)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (var w = new StreamWriter(zip.CreateEntry("about.txt").Open(), new UTF8Encoding(false))) w.Write(about);
        foreach (var (folder, dir) in folders)
        {
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.log"); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                try
                {
                    // Shared, since the program writing it may still have it open.
                    using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var target = zip.CreateEntry($"{folder}/{Path.GetFileName(file)}").Open();
                    source.CopyTo(target);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

/// <summary>Reading the crash and hang reports Windows keeps in its Application event log.</summary>
public static partial class CrashReports
{
    /// <summary>
    /// Which part of IDM-Rea a report is about ("app", "engine", "browser link"), by the program it names: "Faulting
    /// application name:" (Application Error), "Application:" (.NET Runtime) or "The program" (Application Hang).
    /// Null for any other program, even one whose report mentions IDM-Rea's code.
    /// </summary>
    public static string? PartOf(string report) => Program().Match(report) is { Success: true } m
        ? m.Groups[1].Value.ToLowerInvariant() switch { "app" => "app", "engine" => "engine", _ => "browser link" }
        : null;

    [GeneratedRegex(@"^\s*(?:Faulting application name:|Application:|The program)\s*IDMRea\.(App|Engine|BrowserHost)\.exe\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Program();
}
