using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace CoolerScreenControl.Services;

/// <summary>
/// Starts the app when the user logs in to Windows. An app that asks for administrator rights is
/// not started reliably from the normal "Run" registry list (Windows would show the permission
/// prompt at every login), so when we are running elevated we register a scheduled task with the
/// "highest privileges" option instead - that starts silently. Without admin rights (the Store
/// version) the plain registry entry is used.
/// </summary>
public static class AutoStartHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CoolerScreenControl";
    private const string TaskName = "CoolerScreenControl";

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void SetEnabled(bool enabled)
    {
        string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

        if (IsElevated())
        {
            RemoveRunKey();
            if (enabled)
                RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F");
            else
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --minimized");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }

    private static void RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(10000);
            DiagnosticLog.Write($"schtasks {arguments.Split(' ')[0]} exit code {p?.ExitCode}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"schtasks failed: {ex.Message}");
        }
    }
}
