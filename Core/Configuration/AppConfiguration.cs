namespace HyprNetShell.Core.Configuration;

internal sealed class AppConfiguration
{
    public WallpaperConfiguration Wallpaper { get; set; } = new();
    public DisplayConfiguration Display { get; set; } = new();
    public BatteryConfiguration Battery { get; set; } = new();
    public WorldClocksConfiguration WorldClocks { get; set; } = new();
    public List<CompositeWindowConfigurationValue> CompositeWindows { get; set; } = [];
}

internal sealed class WallpaperConfiguration
{
    public bool SlideshowEnabled { get; set; } = true;
    public int DurationMinutes { get; set; } = 10;
}

internal sealed class DisplayConfiguration
{
    public AutomaticCurveConfiguration Temperature { get; set; } = new()
    {
        Enabled = true,
        Points =
        [
            new() { Hour = 0.0f, Value = 3500 },
            new() { Hour = 7.0f, Value = 6000 },
            new() { Hour = 18.0f, Value = 6000 },
            new() { Hour = 24.0f, Value = 3500 },
        ],
    };

    public AutomaticCurveConfiguration Brightness { get; set; } = new()
    {
        Enabled = false,
        Points =
        [
            new() { Hour = 0.0f, Value = 30 },
            new() { Hour = 7.0f, Value = 55 },
            new() { Hour = 12.0f, Value = 100 },
            new() { Hour = 20.0f, Value = 45 },
        ],
    };
}

internal sealed class AutomaticCurveConfiguration
{
    public bool Enabled { get; set; }
    public List<CurvePointConfiguration> Points { get; set; } = [];
}

internal sealed class CurvePointConfiguration
{
    public float Hour { get; set; }
    public int Value { get; set; }
}

internal sealed class BatteryConfiguration
{
    public int? ChargeLimit { get; set; }
}

internal sealed class WorldClocksConfiguration
{
    public List<string> TimeZoneIds { get; set; } =
    [
        "UTC",
        "Europe/Kyiv",
        "Asia/Tel_Aviv",
        "America/New_York",
        "America/Los_Angeles",
        "Asia/Tokyo",
    ];
}

internal sealed class CompositeWindowConfigurationValue
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public List<string> TabIds { get; set; } = [];
}
