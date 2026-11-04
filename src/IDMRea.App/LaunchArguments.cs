namespace IDMRea.App;

/// <summary>What a launch asked for: <c>--job &lt;id&gt;</c> opens that download, <c>--add-clipboard</c> opens the Add
/// dialog, <c>--settings</c> opens Settings. The tray menu and notifications launch the app this way.</summary>
public sealed record LaunchArguments(Guid? Job, bool AddFromClipboard, bool OpenSettings = false)
{
    public static LaunchArguments Parse(string? commandLine)
    {
        var tokens = (commandLine ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('"')).ToArray();
        Guid? job = null;
        for (var i = 0; i < tokens.Length - 1; i++)
            if (tokens[i] == "--job" && Guid.TryParse(tokens[i + 1], out var id)) job = id;
        return new(job, tokens.Contains("--add-clipboard"), tokens.Contains("--settings"));
    }
}
