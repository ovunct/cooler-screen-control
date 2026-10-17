using System.Threading;
using System.Threading.Tasks;
using CoolerScreenControl.Models;

namespace CoolerScreenControl.Services;

/// <summary>
/// Owns the "which item is showing right now" logic. The cooler's screen has a hard, unconditional
/// per-value countdown (see CoolerHidService) capped at roughly 12.75 seconds - if we don't get a
/// write out before that expires, it blanks, for real, no way around it. Early versions ran this
/// loop on a WPF DispatcherTimer, which ticks on the UI thread - and testing showed wildly
/// inconsistent blank-out timings (5s, 10s, 16s...) with identical settings, which pointed to the
/// UI thread occasionally being busy or blocked (a slow sensor read, UI work, anything) long enough
/// to miss that window for real. So this now runs its own background loop, completely off the UI
/// thread, so a slow paint or a blocking sensor call can never delay the write that keeps the
/// screen alive.
/// </summary>
public sealed class CycleEngine
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly CoolerHidService _hid;
    private readonly SystemSensors _sensors;
    private readonly object _itemsLock = new();
    private readonly PomodoroTimer _pomodoro;
    // Shown on its own while the Pomodoro runs in focus mode (not part of the user's list).
    private readonly DisplayItem _pomodoroItem = new() { Type = DisplayItemType.Pomodoro, Enabled = true, DurationValue = 5 };

    private List<DisplayItem> _items = new();
    private DisplayItem? _currentItem;
    private int _secondsOnCurrentItem;
    private int _cachedValue;
    private int _ticksSinceLastSend;
    private DateTime? _lastWriteAt;
    public bool CelsiusMode { get; set; } = true;
    public bool PomodoroFocusOnly { get; set; } = true;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<DisplayItem?>? CurrentItemChanged;
    public event Action<bool>? ConnectionStateChanged;

    public CycleEngine(CoolerHidService hid, SystemSensors sensors, PomodoroTimer pomodoro)
    {
        _hid = hid;
        _sensors = sensors;
        _pomodoro = pomodoro;
    }

    public void UpdateItems(IEnumerable<DisplayItem> items)
    {
        lock (_itemsLock)
        {
            _items = items.ToList();
            // If the currently-showing item got disabled or removed, force re-selection next tick.
            if (_currentItem != null && _currentItem != _pomodoroItem && !_items.Contains(_currentItem))
            {
                _currentItem = null;
                _secondsOnCurrentItem = 0;
            }
        }
    }

    public void Start()
    {
        if (_loopTask != null) return; // already running

        if (!_hid.IsConnected)
        {
            bool ok = _hid.TryConnect();
            ConnectionStateChanged?.Invoke(ok);
            if (!ok) return;
        }

        _currentItem = null;
        _secondsOnCurrentItem = 0;
        _ticksSinceLastSend = 0;

        _lastWriteAt = null;
        DiagnosticLog.Write($"START. Other XPG processes running: {DiagnosticLog.DescribeOtherXpgProcesses()}");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoop(token), token);
    }

    public void Stop()
    {
        DiagnosticLog.Write("STOP.");
        _cts?.Cancel();
        _loopTask = null;
        CurrentItemChanged?.Invoke(null);
    }

    private async Task RunLoop(CancellationToken token)
    {
        Tick(); // show something immediately instead of waiting a full second
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            if (token.IsCancellationRequested) break;
            Tick();
        }
    }

    private void Tick()
    {
        // Do ALL shared-state work inside the lock, but don't call anything that might block on
        // another thread (like a UI event) while holding it - that's how a background thread and
        // the UI thread can end up permanently waiting on each other (a deadlock). We figure out
        // what happened while locked, then fire events afterward with the lock already released.
        if (!_sensors.IsReady) return; // sensors are still starting up in the background
        _sensors.SampleCpuUsage(); // keep the CPU % current even while it is not on screen
        _pomodoro.Update(); // may raise PhaseChanged - do it before taking our lock

        DisplayItem? current;
        bool connectionLost = false;

        lock (_itemsLock)
        {
            var enabled = BuildActiveList();

            if (enabled.Count == 0)
            {
                _currentItem = null;
                current = null;
            }
            else
            {
                bool justSwitched = false;
                if (_currentItem == null || !enabled.Contains(_currentItem))
                {
                    _currentItem = enabled[0];
                    _secondsOnCurrentItem = 0;
                    justSwitched = true;
                }

                current = _currentItem;

                // Only recompute the actual value when we just switched to this item, or when at
                // least RefreshSeconds ticks have passed since we last recomputed it. Either way,
                // we write to the device below every single tick regardless - only the NUMBER
                // inside is throttled.
                bool valueRefreshDue = _ticksSinceLastSend >= Math.Max(0, current.RefreshSeconds)
                                       || current.Type == DisplayItemType.Pomodoro;
                if (justSwitched || valueRefreshDue)
                {
                    _cachedValue = ReadCurrentValue(current);
                    _ticksSinceLastSend = 0;
                }
                else
                {
                    _ticksSinceLastSend++;
                }

                // Always write, every tick, no matter what - this is what's supposed to keep the
                // screen from tripping its own countdown and going dark.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                connectionLost = !SendValue(current, _cachedValue);
                sw.Stop();

                // Log every write: what, which value, ok or not, how long it took, and how long
                // since the previous write (a big gap here means our loop stalled).
                double sinceLast = _lastWriteAt == null ? 0 : (DateTime.Now - _lastWriteAt.Value).TotalSeconds;
                _lastWriteAt = DateTime.Now;
                DiagnosticLog.Write(
                    $"WRITE {(connectionLost ? "FAILED" : "ok")}  {current.DisplayName} = {_cachedValue}  " +
                    $"(took {sw.ElapsedMilliseconds} ms, {sinceLast:0.0}s since previous write)");

                _secondsOnCurrentItem += (int)TickInterval.TotalSeconds;
                if (_secondsOnCurrentItem >= Math.Max(1, current.DurationSeconds))
                {
                    _secondsOnCurrentItem = 0;
                    int index = enabled.IndexOf(current);
                    _currentItem = enabled[(index + 1) % enabled.Count];
                    _ticksSinceLastSend = int.MaxValue; // force a fresh read the moment the new item shows
                }
            }
        }

        // Lock released - safe to raise events now, even if a handler blocks on the UI thread.
        if (connectionLost)
        {
            bool reconnected = _hid.TryConnect();
            ConnectionStateChanged?.Invoke(reconnected);
        }
        CurrentItemChanged?.Invoke(current);
    }

    /// <summary>The items to rotate through right now.</summary>
    private List<DisplayItem> BuildActiveList()
    {
        bool pomodoroRunning = _pomodoro.IsRunning;
        if (pomodoroRunning && PomodoroFocusOnly)
            return new List<DisplayItem> { _pomodoroItem };

        // The Pomodoro item only takes part in the rotation while the timer is actually running.
        // CPU temperature is skipped when it can't be read (no administrator rights).
        bool temperature = _sensors.TemperatureAvailable;
        return _items.Where(i => i.Enabled
                                 && (i.Type != DisplayItemType.Pomodoro || pomodoroRunning)
                                 && (i.Type != DisplayItemType.CpuTemp || temperature)).ToList();
    }

    // The cooler only shows the number we send (the Celsius flag just lights the C/F icon), so for
    // Fahrenheit we must convert the value ourselves.
    private int ReadTemperature()
    {
        int c = _sensors.GetCpuTemperatureCelsius() ?? 0;
        return CelsiusMode ? c : (int)Math.Round(c * 9.0 / 5.0 + 32);
    }

    private int ReadCurrentValue(DisplayItem item)
    {
        switch (item.Type)
        {
            case DisplayItemType.CpuTemp: return ReadTemperature();
            case DisplayItemType.RamUsage: return _sensors.GetRamUsagePercent();
            case DisplayItemType.CpuUsage: return _sensors.GetCpuUsagePercent();
            case DisplayItemType.Custom: return item.CustomValue;
            case DisplayItemType.Pomodoro: return _pomodoro.DisplayNumber();
            default: return 0;
        }
    }

    private bool SendValue(DisplayItem item, int value)
    {
        return item.Type switch
        {
            DisplayItemType.CpuTemp => _hid.ShowCpuTemp(value, CelsiusMode),
            DisplayItemType.RamUsage => _hid.ShowRamUsage(value, CelsiusMode),
            DisplayItemType.CpuUsage => _hid.ShowCpuUsage(value, CelsiusMode),
            DisplayItemType.Custom or DisplayItemType.Pomodoro
                => _hid.ShowCpuTemp(value, CelsiusMode, rawDigitsOnly: true),
            _ => false
        };
    }
}
