namespace CoolerScreenControl.Models;

public enum DisplayItemType
{
    CpuTemp,
    RamUsage,
    CpuUsage,
    Custom,
    Clock,      // removed from the app (the screen can't show a time); kept so old settings files still load
    Pomodoro
}

public enum TimeUnit
{
    Seconds,
    Minutes,
    Hours
}

/// <summary>
/// One entry in the user's rotation. The app cycles through the Enabled items, in the order
/// they appear in the list, showing each one on the cooler's screen for DurationSeconds before
/// moving to the next. If only one item is enabled, it's effectively "locked" to that item.
/// </summary>
public class DisplayItem
{
    public DisplayItemType Type { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The number the user typed for "Shown for", in whatever unit DurationUnit says.</summary>
    public int DurationValue { get; set; } = 5;

    /// <summary>Which unit DurationValue is in - seconds, minutes, or hours.</summary>
    public TimeUnit DurationUnit { get; set; } = TimeUnit.Seconds;

    /// <summary>DurationValue converted to seconds - what the cycling engine actually uses.</summary>
    public int DurationSeconds => DurationUnit switch
    {
        TimeUnit.Minutes => DurationValue * 60,
        TimeUnit.Hours => DurationValue * 3600,
        _ => DurationValue
    };

    /// <summary>
    /// How often (in seconds) the displayed NUMBER is allowed to change while this item is
    /// showing. The app writes to the screen every second no matter what (the screen needs that
    /// to stay lit), but this setting throttles how often that written value is actually
    /// recomputed from the live sensor, so a jittery metric like CPU Usage % can hold visually
    /// still instead of changing every second. Switching to this item always shows a fresh value
    /// immediately regardless of this setting.
    /// </summary>
    public int RefreshSeconds { get; set; } = 2;

    /// <summary>Only used when Type == Custom: the fixed number to display (0-255).</summary>
    public byte CustomValue { get; set; } = 0;

    public string DisplayName => Type switch
    {
        DisplayItemType.CpuTemp => "CPU Temperature",
        DisplayItemType.RamUsage => "RAM Usage %",
        DisplayItemType.CpuUsage => "CPU Usage %",
        DisplayItemType.Custom => $"Custom Number ({CustomValue})",
        DisplayItemType.Pomodoro => "Pomodoro timer",
        _ => Type.ToString()
    };
}
