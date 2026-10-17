using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace CoolerScreenControl;

public partial class App : Application
{
    // Only one copy of this app should ever talk to the cooler at once.
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "CoolerScreenControl_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Cooler Screen Control is already running (check the system tray).",
                "Already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        CoolerScreenControl.Services.DiagnosticLog.StartNewSession();
        CoolerScreenControl.Services.ThemeManager.Apply(false); // colours must exist before the window loads
        base.OnStartup(e);
    }
}
