using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace CoolerScreenControl.Services;

/// <summary>
/// Reads the three values we actually need. CPU temperature needs Administrator rights on
/// Windows to access hardware sensors - this is true of every tool that reads real CPU
/// temperature (HWiNFO, LibreHardwareMonitor, NZXT CAM, etc.), not something specific to us.
/// The app's manifest requests admin at launch for this reason.
/// </summary>
public sealed class SystemSensors : IDisposable
{
    private Computer? _computer;
    private PerformanceCounter? _cpuCounter;
    private volatile bool _isReady;
    private volatile bool _temperatureAvailable;

    /// <summary>True when an exact CPU temperature can be read (needs administrator rights).</summary>
    public bool TemperatureAvailable => _temperatureAvailable;

    /// <summary>True once Initialize() has finished.</summary>
    public bool IsReady => _isReady;

    /// <summary>
    /// Opening the hardware monitor driver and creating the performance counter are slow (seconds),
    /// so they are NOT done in the constructor. Call this from a background thread at startup so
    /// the window can appear immediately.
    /// </summary>
    public void Initialize()
    {
        _computer = new Computer { IsCpuEnabled = true };
        try
        {
            _computer.Open();
        }
        catch
        {
            // If this fails (e.g. not running as Administrator), GetCpuTemperature() below
            // will just keep returning null instead of throwing.
        }

        try
        {
            // "% Processor Utility" is what Task Manager shows (frequency-adjusted); the older
            // "% Processor Time" reads differently. Fall back to it if Utility isn't available.
            try { _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total"); }
            catch { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
            _cpuCounter.NextValue(); // first read is always 0 - prime it
        }
        catch
        {
            _cpuCounter = null;
        }
        _ = GetCpuTemperatureExact(); // sets TemperatureAvailable
        _isReady = true;
    }

    /// <summary>CPU package temperature in Celsius, or null if it couldn't be read.</summary>
    public int? GetCpuTemperatureCelsius()
    {
        var exact = GetCpuTemperatureExact();
        return exact.HasValue ? (int)Math.Round(exact.Value) : null;
    }

    /// <summary>CPU temperature in Celsius with decimals, or null if it couldn't be read.</summary>
    public float? GetCpuTemperatureExact()
    {
        var value = GetHardwareMonitorTemperature();
        _temperatureAvailable = value.HasValue;
        return value;
    }

    /// <summary>Exact reading through the hardware-monitor driver (needs admin rights), or null.</summary>
    private float? GetHardwareMonitorTemperature()
    {
        try
        {
            if (_computer == null) return null;
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu) continue;
                hardware.Update();

                // Prefer a "Package" sensor if present, otherwise take the highest core reading.
                ISensor? best = null;
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue) continue;
                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        return sensor.Value!.Value;
                    if (best == null || sensor.Value > best.Value) best = sensor;
                }
                if (best != null) return best.Value!.Value;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>RAM usage as a whole-number percentage (0-100). Doesn't need admin rights.</summary>
    public int GetRamUsagePercent()
    {
        var status = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(status))
        {
            return (int)status.dwMemoryLoad;
        }
        return 0;
    }

    /// <summary>CPU usage as a whole-number percentage (0-100). Doesn't need admin rights.</summary>
    public int GetCpuUsagePercent() => _lastCpuPercent;

    private volatile int _lastCpuPercent;

    /// <summary>
    /// Reads the CPU counter and remembers the result. Call this once a second, whether or not CPU
    /// usage is on screen: the counter reports the average since its previous read, so reading it
    /// only when the item appears (every 10+ seconds) gives a long-run average that doesn't match
    /// Task Manager's current figure.
    /// </summary>
    public void SampleCpuUsage()
    {
        try
        {
            _lastCpuPercent = (int)Math.Clamp(Math.Round(_cpuCounter?.NextValue() ?? 0), 0, 100);
        }
        catch
        {
            // keep the previous value
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); } catch { /* ignore */ }
        _cpuCounter?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
