using System.Diagnostics;
using System.IO;

namespace CoolerScreenControl.Services;

/// <summary>
/// Writes one line per event to %AppData%\CoolerScreenControl\log.txt so we can see exactly what the
/// app sent to the cooler, when, whether it succeeded, and how long each write took. Also records
/// whether XPG-Prime / xpgservice were running at startup, since two apps writing to the same
/// screen at once would make it behave unpredictably.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();

    public static readonly string FolderPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoolerScreenControl");

    public static readonly string FilePath = Path.Combine(FolderPath, "log.txt");

    public static void StartNewSession()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            lock (Gate)
            {
                // Start fresh each launch so the file stays small and easy to read.
                File.WriteAllText(FilePath, $"=== Cooler Screen Control log, started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
            }
            Write($"Other XPG processes running right now: {DescribeOtherXpgProcesses()}");
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    /// <summary>Lists any running process whose name looks like XPG's own software.</summary>
    public static string DescribeOtherXpgProcesses()
    {
        try
        {
            int ownId = Environment.ProcessId;
            var names = Process.GetProcesses()
                .Where(p => p.Id != ownId &&
                            (p.ProcessName.Contains("xpg", StringComparison.OrdinalIgnoreCase) ||
                             p.ProcessName.Contains("prime", StringComparison.OrdinalIgnoreCase)))
                .Select(p => $"{p.ProcessName} (pid {p.Id})")
                .ToList();
            return names.Count == 0 ? "none" : string.Join(", ", names);
        }
        catch
        {
            return "unknown (couldn't list processes)";
        }
    }
}
