using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CoolerScreenControl.Models;
using CoolerScreenControl.Services;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace CoolerScreenControl;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DisplayItem> _items = new();
    private readonly CoolerHidService _hid = new();
    private readonly SystemSensors _sensors = new();
    private readonly PomodoroTimer _pomodoro = new();
    private readonly CycleEngine _engine;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private Task _sensorInit = Task.CompletedTask;
    private Forms.NotifyIcon? _trayIcon;
    private bool _running;
    private bool _reallyClosing;

    // Some checkboxes have their initial value set directly in XAML (e.g. CelsiusCheck
    // IsChecked="True"), which fires their Checked/Unchecked event during InitializeComponent()
    // - before this constructor has had a chance to create _engine or finish loading settings.
    // This flag makes those handlers do nothing until we're actually ready.
    private bool _isReady;
    private byte _delayByte = 0;
    private Dictionary<int, byte> _rawOverrides = new();

    public MainWindow()
    {
        _engine = new CycleEngine(_hid, _sensors, _pomodoro);
        _pomodoro.PhaseChanged += OnPomodoroPhaseChanged;
        _engine.CurrentItemChanged += OnCurrentItemChanged;
        _engine.ConnectionStateChanged += OnConnectionStateChanged;

        InitializeComponent();

        // Sensor start-up is slow (seconds) - do it off the UI thread so the window appears at once.
        _sensorInit = Task.Run(() => _sensors.Initialize());

        ItemsList.ItemsSource = _items;

        LoadSettings();
        _isReady = true;

        SetupTrayIcon();

        // Refreshes the Pomodoro countdown text once a second. This only updates a label - the
        // cooler itself is written to by the engine's own background loop.
        _uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => UpdatePomodoroUi();
        _uiTimer.Start();

        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized");
        if (startMinimized)
        {
            Hide();
        }

        // The slow start-up work (looking for other cooler software, connecting to the cooler,
        // starting the sensors) happens after the window is already on screen.
        // (Queued rather than tied to the Loaded event, so it also runs when started hidden in the tray.)
        Dispatcher.BeginInvoke(new Action(StartupAsync), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private async void StartupAsync()
    {
        bool startMinimized = Environment.GetCommandLineArgs().Contains("--minimized");

        // If the cooler maker's own software is running, it writes to the same screen as we do and
        // the two apps fight over it (random flashing and black gaps). Warn clearly instead of
        // silently misbehaving.
        string others = await Task.Run(DiagnosticLog.DescribeOtherXpgProcesses);
        if (others != "none" && !startMinimized)
        {
            MessageBox.Show(
                "The cooler maker's own software seems to be running:\n\n" + others +
                "\n\nIt writes to the same cooler screen as this app, so the two will fight over it " +
                "(flashing, random black gaps). Please close it in Task Manager (End task on each of " +
                "these), then press Stop and Start in this app.",
                "Other cooler software is running", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Auto-start the rotation as soon as the window opens - no reason to make the user
        // click Start every time, especially when launching at Windows login.
        _engine.CelsiusMode = CelsiusCheck.IsChecked == true;
        _engine.UpdateItems(_items);
        await Task.Run(() => _engine.Start()); // connecting to the cooler can take a moment
        await _sensorInit;
        if (!_sensors.TemperatureAvailable)
            TemperatureNote.Visibility = Visibility.Visible;
        _running = true;
        StartStopButton.Content = "Stop";
    }

    // Dark title bar (Windows 10 20H1 and newer; silently does nothing on older versions).
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetDarkTitleBar(DarkModeCheck.IsChecked == true);
    }

    private void SetDarkTitleBar(bool dark)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int on = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
        }
        catch { /* cosmetic only */ }
    }

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        bool dark = DarkModeCheck.IsChecked == true;
        ThemeManager.Apply(dark);
        SetDarkTitleBar(dark);
        SaveSettings();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{DiagnosticLog.FilePath}\"");
        }
        catch
        {
            MessageBox.Show(DiagnosticLog.FilePath, "Log file location");
        }
    }

    // ---------------- Settings ----------------

    private void LoadSettings()
    {
        var settings = SettingsStore.Load();
        _items.Clear();
        // The Clock item was removed - drop it from older settings files.
        foreach (var item in settings.Items.Where(i => i.Type != DisplayItemType.Clock)) _items.Add(item);
        // Older settings files don't have the Pomodoro item - add it (switched off).
        if (!_items.Any(i => i.Type == DisplayItemType.Pomodoro))
            _items.Add(new DisplayItem { Type = DisplayItemType.Pomodoro, Enabled = false, DurationValue = 5 });
        WorkMinutesBox.Text = settings.PomodoroWorkMinutes.ToString();
        BreakMinutesBox.Text = settings.PomodoroBreakMinutes.ToString();
        PomodoroFocusCheck.IsChecked = settings.PomodoroFocusOnly;
        _pomodoro.WorkMinutes = settings.PomodoroWorkMinutes;
        _pomodoro.BreakMinutes = settings.PomodoroBreakMinutes;
        _engine.PomodoroFocusOnly = settings.PomodoroFocusOnly;

        ThemeManager.Apply(settings.DarkMode);
        DarkModeCheck.IsChecked = settings.DarkMode;
        CelsiusCheck.IsChecked = settings.CelsiusMode;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
        MinimizeToTrayCheck.IsChecked = settings.StartMinimizedToTray;
        _hid.DelayByte = settings.DelayByte;
        ShowLabelsCheck.IsChecked = settings.ShowLabels;
        _hid.ShowLabels = settings.ShowLabels;
        _hid.RawOverrides = settings.RawOverrides ?? new();
        _rawOverrides = _hid.RawOverrides;
        _delayByte = settings.DelayByte;
        _engine.CelsiusMode = settings.CelsiusMode;
        _engine.UpdateItems(_items);
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            Items = _items.ToList(),
            CelsiusMode = CelsiusCheck.IsChecked == true,
            StartWithWindows = StartWithWindowsCheck.IsChecked == true,
            DarkMode = DarkModeCheck.IsChecked == true,
            StartMinimizedToTray = MinimizeToTrayCheck.IsChecked == true,
            DelayByte = _delayByte,
            ShowLabels = ShowLabelsCheck.IsChecked == true,
            RawOverrides = _rawOverrides,
            PomodoroWorkMinutes = _pomodoro.WorkMinutes,
            PomodoroBreakMinutes = _pomodoro.BreakMinutes,
            PomodoroFocusOnly = PomodoroFocusCheck.IsChecked == true
        };
        SettingsStore.Save(settings);
    }

    // ---------------- Engine control ----------------

    private void StartEngine()
    {
        _engine.CelsiusMode = CelsiusCheck.IsChecked == true;
        _engine.UpdateItems(_items);
        _engine.Start();
        _running = true;
        StartStopButton.Content = "Stop";
    }

    private void StopEngine()
    {
        _engine.Stop();
        _hid.Disconnect();
        _running = false;
        StartStopButton.Content = "Start";
        StatusText.Text = "Stopped.";
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running) StopEngine();
        else StartEngine();
    }

    private void OnCurrentItemChanged(DisplayItem? item)
    {
        // BeginInvoke (not Invoke) - these fire from the engine's background thread, and a
        // fire-and-forget status update should never risk blocking that thread waiting on the UI
        // thread, however briefly.
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = item == null
                ? "Nothing enabled - turn at least one item on."
                : $"Showing: {item.DisplayName}";
        });
    }

    private void OnConnectionStateChanged(bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!connected)
                StatusText.Text = "Can't find the cooler - is it plugged in, and is the cooler maker's own software closed?";
        });
    }

    // ---------------- List editing ----------------

    private void AnySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;

        // If the box was left empty (or with something unparsable) when focus left it, the
        // number binding silently keeps whatever the item's last valid value was - refresh the
        // box's displayed text from that value so it doesn't just look blank.
        if (sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        _engine.CelsiusMode = CelsiusCheck.IsChecked == true;
        _hid.ShowLabels = ShowLabelsCheck.IsChecked == true;
        _engine.PomodoroFocusOnly = PomodoroFocusCheck.IsChecked == true;
        _engine.UpdateItems(_items);
        SaveSettings();
    }

    // ---------------- Pomodoro ----------------

    private static int ParseMinutes(string text, int fallback)
        => int.TryParse(text, out var v) ? Math.Clamp(v, 1, 180) : fallback;

    private void PomodoroSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        _pomodoro.WorkMinutes = ParseMinutes(WorkMinutesBox.Text, _pomodoro.WorkMinutes);
        _pomodoro.BreakMinutes = ParseMinutes(BreakMinutesBox.Text, _pomodoro.BreakMinutes);
        WorkMinutesBox.Text = _pomodoro.WorkMinutes.ToString();
        BreakMinutesBox.Text = _pomodoro.BreakMinutes.ToString();
        SaveSettings();
    }

    private void PomodoroStart_Click(object sender, RoutedEventArgs e) => TogglePomodoro();

    private void PomodoroReset_Click(object sender, RoutedEventArgs e)
    {
        _pomodoro.Reset();
        UpdatePomodoroUi();
    }

    private void TogglePomodoro()
    {
        // Make sure typed minutes are applied even if a box still has focus.
        _pomodoro.WorkMinutes = ParseMinutes(WorkMinutesBox.Text, _pomodoro.WorkMinutes);
        _pomodoro.BreakMinutes = ParseMinutes(BreakMinutesBox.Text, _pomodoro.BreakMinutes);
        if (_pomodoro.IsRunning) _pomodoro.Pause();
        else _pomodoro.Start();
        UpdatePomodoroUi();
    }

    private void UpdatePomodoroUi()
    {
        var phase = _pomodoro.Phase;
        var r = _pomodoro.Remaining;
        string time = $"{(int)r.TotalMinutes:00}:{r.Seconds:00}";
        PomodoroStartButton.Content = _pomodoro.IsRunning ? "Pause" : (_pomodoro.IsPaused ? "Resume" : "Start");
        PomodoroStatusText.Text = phase switch
        {
            PomodoroPhase.Idle => "Not running. The cooler shows minutes left, then seconds in the last minute.",
            PomodoroPhase.Work => $"Work: {time} left{(_pomodoro.IsPaused ? " (paused)" : "")}.  Sessions done: {_pomodoro.CompletedWorkSessions}",
            _ => $"Break: {time} left{(_pomodoro.IsPaused ? " (paused)" : "")}.  Sessions done: {_pomodoro.CompletedWorkSessions}"
        };
    }

    private void OnPomodoroPhaseChanged(PomodoroPhase phase)
    {
        // Raised from the engine's background thread.
        Dispatcher.BeginInvoke(() =>
        {
            System.Media.SystemSounds.Asterisk.Play();
            string msg = phase == PomodoroPhase.Break ? "Work session done - take a break." : "Break is over - back to work.";
            _trayIcon?.ShowBalloonTip(4000, "Pomodoro", msg, Forms.ToolTipIcon.Info);
            UpdatePomodoroUi();
        });
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        if (!byte.TryParse(CustomValueBox.Text, out var value))
        {
            MessageBox.Show("Enter a whole number from 0 to 255.", "Invalid number");
            return;
        }
        _items.Add(new DisplayItem { Type = DisplayItemType.Custom, Enabled = true, DurationValue = 5, CustomValue = value });
        _engine.UpdateItems(_items);
        SaveSettings();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is DisplayItem item)
        {
            _items.Remove(item);
            _engine.UpdateItems(_items);
            SaveSettings();
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DisplayItem item })
        {
            int index = _items.IndexOf(item);
            if (index > 0) _items.Move(index, index - 1);
            _engine.UpdateItems(_items);
            SaveSettings();
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DisplayItem item })
        {
            int index = _items.IndexOf(item);
            if (index >= 0 && index < _items.Count - 1) _items.Move(index, index + 1);
            _engine.UpdateItems(_items);
            SaveSettings();
        }
    }

    private void StartWithWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        AutoStartHelper.SetEnabled(StartWithWindowsCheck.IsChecked == true);
        SaveSettings();
    }

    // ---------------- Tray icon / minimize behavior ----------------

    private void SetupTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Cooler Screen Control"
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Pomodoro: start / pause", null, (_, _) => Dispatcher.BeginInvoke(TogglePomodoro));
        menu.Items.Add("Pomodoro: reset", null, (_, _) => Dispatcher.BeginInvoke(() => { _pomodoro.Reset(); UpdatePomodoroUi(); }));
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _reallyClosing = true;
            Close();
        });
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && MinimizeToTrayCheck.IsChecked == true)
        {
            Hide();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Closing the window (the X button) minimizes to tray instead of quitting, unless the
        // user picked Exit from the tray menu - keeps the screen alive in the background, same
        // as XPG-Prime did, instead of surprising the user by killing the display.
        if (!_reallyClosing && MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _engine.Stop();
        _hid.Dispose();
        _sensors.Dispose();
        _trayIcon?.Dispose();
    }
}
