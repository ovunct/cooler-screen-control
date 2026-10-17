using System.IO;
using System.Text.Json;
using CoolerScreenControl.Models;

namespace CoolerScreenControl.Services;

public class AppSettings
{
    public List<DisplayItem> Items { get; set; } = new()
    {
        new DisplayItem { Type = DisplayItemType.CpuTemp, Enabled = true, DurationValue = 5 },
        new DisplayItem { Type = DisplayItemType.RamUsage, Enabled = true, DurationValue = 5 },
        new DisplayItem { Type = DisplayItemType.CpuUsage, Enabled = true, DurationValue = 5 },
        new DisplayItem { Type = DisplayItemType.Pomodoro, Enabled = false, DurationValue = 5 },
    };
    public bool CelsiusMode { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;

    /// <summary>Packet byte [6] (cooler's own rotation time). Edit here to test values; 0-255.</summary>
    public byte DelayByte { get; set; } = 0;

    /// <summary>Show the label/unit (RAM, %, CPU, °C). They blink as the cooler rotates its slots.</summary>
    public bool ShowLabels { get; set; } = false;

    public bool DarkMode { get; set; } = false;

    public int PomodoroWorkMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;
    /// <summary>While the Pomodoro runs, show only the timer on the cooler.</summary>
    public bool PomodoroFocusOnly { get; set; } = true;

    /// <summary>Experiments: raw packet byte overrides, e.g. {"11": 15}. Leave empty normally.</summary>
    public Dictionary<int, byte> RawOverrides { get; set; } = new();
}

/// <summary>Loads/saves settings as plain JSON under %AppData%\CoolerScreenControl\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string FolderPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoolerScreenControl");

    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            // The app used to be called XpgCoolerControl - carry the old settings over once.
            if (!File.Exists(FilePath))
            {
                var oldFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XpgCoolerControl", "settings.json");
                if (File.Exists(oldFile))
                {
                    Directory.CreateDirectory(FolderPath);
                    File.Copy(oldFile, FilePath);
                }
            }

            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Fall through to defaults if the file is missing or corrupt.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort - not fatal if a save fails.
        }
    }
}
