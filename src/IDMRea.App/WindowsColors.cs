using IDMRea.Contracts;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;
namespace IDMRea.App;

/// <summary>The Windows accent colour and light or dark mode, read live so the app and its icon follow them.</summary>
internal sealed class WindowsColors
{
    private static readonly (string Key, UIColorType Type)[] AccentKeys =
    [
        ("SystemAccentColor", UIColorType.Accent),
        ("SystemAccentColorDark1", UIColorType.AccentDark1), ("SystemAccentColorDark2", UIColorType.AccentDark2), ("SystemAccentColorDark3", UIColorType.AccentDark3),
        ("SystemAccentColorLight1", UIColorType.AccentLight1), ("SystemAccentColorLight2", UIColorType.AccentLight2), ("SystemAccentColorLight3", UIColorType.AccentLight3),
    ];
    private readonly UISettings settings = new(); // kept alive: it raises Changed

    /// <summary>Raised on a background thread when the accent or light/dark mode changes in Windows.</summary>
    public event Action? Changed;

    public WindowsColors() => settings.ColorValuesChanged += (_, _) => Changed?.Invoke();

    private static Rgb ToRgb(Windows.UI.Color c) => new(c.R, c.G, c.B);

    /// <summary>The icon's ring: the accent, or its lighter shade if the accent would vanish on the dark tile.</summary>
    public Rgb Ring => IconArt.RingFor(ToRgb(settings.GetColorValue(UIColorType.Accent)), ToRgb(settings.GetColorValue(UIColorType.AccentLight2)));

    /// <summary>Windows itself is in dark mode (its app background is dark).</summary>
    public bool SystemIsDark => settings.GetColorValue(UIColorType.Background).R < 128;

    /// <summary>Puts the current accent shades into the app's resources, so controls pick them up on their next theme pass.</summary>
    public void CopyAccentTo(ResourceDictionary resources)
    {
        foreach (var (key, type) in AccentKeys) resources[key] = settings.GetColorValue(type);
    }
}
