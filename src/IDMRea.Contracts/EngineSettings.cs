using System.Text.Json.Serialization;

namespace IDMRea.Contracts;

public enum CloseBehavior { KeepInTray, PauseAllAndQuit }
public enum AppTheme { System, Light, Dark }

public sealed record EngineSettings(int DownloadsAtOnce = 3, int PartsPerFile = 8, long SpeedLimitBytesPerSecond = 0,
    int RetryAttempts = 5, bool UltraPerformance = false, CloseBehavior OnClose = CloseBehavior.KeepInTray,
    bool StartWithWindows = false, bool NotifyOnComplete = true, bool NotifyOnFailure = true,
    AppTheme Theme = AppTheme.System, IReadOnlyDictionary<Category, string>? Folders = null)
{
    public EngineSettings Clamped() => this with
    {
        DownloadsAtOnce = Math.Clamp(DownloadsAtOnce, 1, 10),
        PartsPerFile = Math.Clamp(PartsPerFile, 1, 16),
        RetryAttempts = Math.Clamp(RetryAttempts, 0, 10),
        SpeedLimitBytesPerSecond = Math.Max(0, SpeedLimitBytesPerSecond),
    };

    /// <summary>Parts per file in use: Ultra performance fixes it at 16.</summary>
    [JsonIgnore] public int EffectiveParts => UltraPerformance ? 16 : Math.Clamp(PartsPerFile, 1, 16);
    [JsonIgnore] public int BufferSize => UltraPerformance ? 1024 * 1024 : 256 * 1024;
    [JsonIgnore] public long MinPartSize => UltraPerformance ? 512 * 1024 : 1024 * 1024;

    public string FolderFor(Category category) =>
        Folders != null && Folders.TryGetValue(category, out var folder) && !string.IsNullOrWhiteSpace(folder) ? folder : DefaultFolder(category);

    public static string DefaultFolder(Category category)
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return category == Category.Other ? downloads : Path.Combine(downloads, category.ToString());
    }
}
