using IDMRea.Contracts;
namespace IDMRea.Engine.Windows;

public enum TrayCommand { Open = 1, AddFromClipboard, PauseAll, ResumeAll, OpenFolder, Settings, Quit, SpeedLimit }

/// <summary>A menu choice; <see cref="Value"/> is the speed limit in bytes per second for <see cref="TrayCommand.SpeedLimit"/>.</summary>
public sealed record TrayChoice(TrayCommand Command, long Value = 0);

/// <summary>What the menu shows about the engine at the moment it opens.</summary>
public sealed record TrayState(int Downloading, long BytesPerSecond, bool CanPause, bool CanResume, long SpeedLimit);

public sealed record TrayMenuItem(string Text, int Id = 0, bool Enabled = true, bool Checked = false, bool Default = false,
    IReadOnlyList<TrayMenuItem>? Items = null)
{
    public bool Separator => Text.Length == 0;
    public static readonly TrayMenuItem Line = new("");
}

/// <summary>The tray icon's right-click menu, built fresh each time it opens.</summary>
public static class TrayMenu
{
    private const int LimitBase = 100;
    private const long KiB = 1024, MiB = 1024 * KiB;
    private static readonly (long Bytes, string Text)[] Limits =
        [(0, "No limit"), (512 * KiB, "512 KB/s"), (MiB, "1 MB/s"), (2 * MiB, "2 MB/s"), (5 * MiB, "5 MB/s"), (10 * MiB, "10 MB/s")];

    public static IReadOnlyList<TrayMenuItem> Build(TrayState state)
    {
        var limits = Limits.Select((l, i) => new TrayMenuItem(l.Text, LimitBase + i, Checked: l.Bytes == state.SpeedLimit)).ToList();
        // A limit typed in Settings stays visible and ticked, so the menu never hides what is in force.
        if (Limits.All(l => l.Bytes != state.SpeedLimit))
            limits.Insert(0, new TrayMenuItem($"{Text.Speed(state.SpeedLimit)} (from Settings)", Enabled: false, Checked: true));
        return
        [
            new(state.Downloading == 0 ? "No downloads running" : $"{state.Downloading} downloading, {Text.Speed(state.BytesPerSecond)}", Enabled: false),
            TrayMenuItem.Line,
            new("Open IDM-Rea", (int)TrayCommand.Open, Default: true),
            new("Add from clipboard", (int)TrayCommand.AddFromClipboard),
            TrayMenuItem.Line,
            new("Pause all", (int)TrayCommand.PauseAll, state.CanPause),
            new("Resume all", (int)TrayCommand.ResumeAll, state.CanResume),
            new("Speed limit", Items: limits),
            TrayMenuItem.Line,
            new("Open downloads folder", (int)TrayCommand.OpenFolder),
            new("Settings", (int)TrayCommand.Settings),
            TrayMenuItem.Line,
            new("Quit", (int)TrayCommand.Quit),
        ];
    }

    /// <summary>The choice behind a menu item id, or null for no choice.</summary>
    public static TrayChoice? Decode(int id) => id switch
    {
        >= LimitBase when id - LimitBase < Limits.Length => new(TrayCommand.SpeedLimit, Limits[id - LimitBase].Bytes),
        > 0 when id < (int)TrayCommand.SpeedLimit => new((TrayCommand)id),
        _ => null,
    };
}
