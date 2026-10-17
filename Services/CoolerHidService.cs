using HidSharp;

namespace CoolerScreenControl.Services;

/// <summary>
/// Talks to the XPG Maestro Plus 42SA cooler screen using the exact packet layout found in
/// XPG-Prime's own source code (backend/protocol/device/aircooling/MaestroPlus42saSeries.js,
/// GetCPUZInformation()). See the project notes for how this was found.
///
/// 64-byte HID Feature Report, all bytes not listed below are 0:
///   [0]  = 0x07  Report ID
///   [1]  = 0xA1  Command
///   [2]  = 1 = Celsius, 0 = Fahrenheit
///   [6]  = "Delay time" per XPG's own source comment. Confirmed by testing to be a literal,
///          UNCONDITIONAL countdown timer, roughly value * 50ms, after which the screen blanks -
///          value 10 (XPG's own default) lasts well under a second; value 100 lasted exactly ~5
///          seconds; value 200 lasted exactly ~10 seconds. Critically, we tried two different
///          "keep-alive" tricks (toggling an unused byte, then toggling a spare bit inside the
///          group bitmask byte) to try to make repeated writes look "new" and reset this - neither
///          had ANY effect, always blanking at exactly the configured value. So this really does
///          look like a hard, unconditional per-value display duration, not a resettable watchdog.
///          Because it's a single byte, ~12.75s (0xFF) is the hard ceiling for how long any one
///          value can be shown, however often we resend it. We set it near that ceiling and rely
///          on the app's own "Shown for" durations staying comfortably under it.
///   [7]  = CPU temperature value (single byte)
///   [8]  = decimal-point control (0x0A = no decimal shown)
///   [9]  = RAM usage % value
///   [10] = CPU usage % value
///   [11] = CPU-Temp group bitmask: bit0=CPU label, bit1=°C icon, bit2=digits
///   [12] = RAM-Usage group bitmask: bit0=RAM label, bit1=%, bit2=digits
///   [13] = CPU-Usage group bitmask: bit0=CPU label, bit1=%, bit2=digits
/// </summary>
public sealed class CoolerHidService : IDisposable
{
    private const int VendorId = 0x125F;
    private const int ProductId = 0x7602;
    private const int ReportLength = 64;

    /// <summary>
    /// Byte [6] of the packet - a hard, unconditional countdown-to-blank timer, in units of
    /// roughly 50ms (0.05s) each. Set as close to the single-byte maximum (~12.75s) as possible,
    /// since nothing we send can extend a value's display beyond this once it's shown - the app's
    /// own "Shown for" durations need to stay comfortably under this ceiling.
    /// </summary>
    private const double CountdownUnitSeconds = 0.05;
    private const double CountdownSeconds = 12.5; // just under the ~12.75s ceiling a byte allows
    private static readonly byte DefaultCountdownByte =
        (byte)Math.Clamp(Math.Round(CountdownSeconds / CountdownUnitSeconds), 0, 255);

    /// <summary>
    /// Packet byte [6], the cooler's own per-slot rotation time. Editable via "DelayByte" in
    /// settings.json so different values can be tested without rebuilding.
    /// </summary>
    public byte DelayByte { get; set; } = 0;

    /// <summary>
    /// When false (default): all three slots show digits only, and the cooler's own rotation is
    /// made fast (byte [6] = 10), so the number is always lit and changes apply almost at once.
    /// When true: the wanted slot also shows its label/unit, but the label then appears and
    /// disappears as the cooler rotates, and DelayByte (slow) is used so updates are slower.
    /// </summary>
    public bool ShowLabels { get; set; } = false;

    private const byte FastDelayByte = 10; // XPG's own default

    /// <summary>
    /// Experiment hook: raw byte overrides applied to every packet just before sending
    /// (key = byte index 0-63, value = byte). Set via "RawOverrides" in settings.json.
    /// </summary>
    public Dictionary<int, byte> RawOverrides { get; set; } = new();

    private HidStream? _stream;

    public bool IsConnected => _stream != null;

    /// <summary>Tries to open the cooler's HID interface. Returns false if it isn't found or is busy.</summary>
    public bool TryConnect()
    {
        Disconnect();
        try
        {
            var device = DeviceList.Local.GetHidDevices(VendorId, ProductId).FirstOrDefault();
            if (device == null)
            {
                DiagnosticLog.Write("CONNECT failed: cooler not found (VID 125F / PID 7602).");
                return false;
            }

            _stream = device.Open();
            _stream.ReadTimeout = 1000;
            _stream.WriteTimeout = 1000;
            DiagnosticLog.Write($"CONNECT ok: {device.GetProductName()}. Countdown byte [6] = {DelayByte}.");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"CONNECT failed: {ex.GetType().Name}: {ex.Message}");
            _stream = null;
            return false;
        }
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>
    /// The cooler rotates through its three slots (CPU temp, RAM, CPU usage) on its own. Instead of
    /// fighting that, we put the SAME number in all three slots, so whichever slot is on screen
    /// shows our number. The wanted slot gets label + unit (0x07); the other two show digits only
    /// (0x04), so the number never goes dark.
    /// </summary>
    private bool ShowNumber(int value, bool celsius, int labelSlot, bool noLabelAtAll = false)
    {
        var packet = NewPacket(celsius);
        byte v = Clamp(value);
        packet[7] = v;   // slot 0: CPU temp
        packet[9] = v;   // slot 1: RAM %
        packet[10] = v;  // slot 2: CPU %
        packet[11] = 0x04;
        packet[12] = 0x04;
        packet[13] = 0x04;
        if (ShowLabels && !noLabelAtAll)
        {
            // Labels belong to individual slots and the cooler rotates through the slots by itself,
            // so they blink. Lighting a label in only the wanted slot makes all of that item's
            // labels (e.g. CPU and C) appear and disappear TOGETHER - calmer than staggered blinking.
            packet[11 + labelSlot] = 0x07;
            // The screen has only a fixed "°C" icon, and the Fahrenheit flag does not change it
            // (tested). In Fahrenheit, don't light it, so the screen never shows a wrong unit.
            if (labelSlot == 0 && !celsius) packet[11] = 0x05;
        }
        return Send(packet);
    }

    public bool ShowCpuTemp(int temperature, bool celsius, bool rawDigitsOnly = false)
        => ShowNumber(temperature, celsius, 0, noLabelAtAll: rawDigitsOnly);

    public bool ShowRamUsage(int percent, bool celsius) => ShowNumber(percent, celsius, 1);

    public bool ShowCpuUsage(int percent, bool celsius) => ShowNumber(percent, celsius, 2);

    private byte[] NewPacket(bool celsius)
    {
        var packet = new byte[ReportLength];
        packet[0] = 0x07;
        packet[1] = 0xA1;
        packet[2] = (byte)(celsius ? 1 : 0);
        packet[6] = ShowLabels ? DelayByte : FastDelayByte;
        packet[8] = 0x0A; // no decimal point
        return packet;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private bool Send(byte[] packet)
    {
        if (_stream == null) return false;
        try
        {
            foreach (var kv in RawOverrides)
                if (kv.Key >= 2 && kv.Key < ReportLength) packet[kv.Key] = kv.Value;
            _stream.SetFeature(packet);
            return true;
        }
        catch (Exception ex)
        {
            // Device likely unplugged or another app grabbed it - caller should try reconnecting.
            DiagnosticLog.Write($"WRITE failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
    }

    public void Dispose() => Disconnect();
}
