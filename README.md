# Cooler Screen Control

A small Windows app that controls the little display on the **XPG Maestro Plus 42SA** CPU cooler.
It replaces the cooler maker's own software for this one job: you choose what the screen shows,
in which order, and for how long - and the number stays steady instead of flashing or going dark.

> Independent hobby project. Not affiliated with, endorsed by, or supported by XPG or ADATA.
> "XPG" is a trademark of its owner and is used here only to say which cooler this works with.

## What it can show
- CPU temperature, RAM usage %, CPU usage % (each on or off, in any order)
- A Pomodoro timer (work / break, minutes left on the cooler screen)
- Any fixed number you like (0-255)
- Celsius or Fahrenheit, optional labels (CPU / RAM / % / °C), dark mode, tray icon, start with Windows

Limits of the cooler's hardware (found by testing): the screen only shows numbers 0-255 - no text,
no clock. Its labels (CPU / RAM / % / °C) blink on their own, so they are optional and off by default.

## Install (no programming needed)
1. Download the latest `CoolerScreenControl-win-x64.zip` from the **Releases** page.
2. Right-click the zip, choose **Extract All**, and open the extracted folder. (Don't run it from inside the zip.)
3. **Close the cooler maker's own software first** (XPG Prime and its service) - two programs can't control the screen at once.
4. Double-click `CoolerScreenControl.exe`.
5. Windows asks for permission (the blue/yellow shield window) - click **Yes**. The app needs it to read your real CPU temperature.
6. If Windows says **"Windows protected your PC"**: click **More info**, then **Run anyway**.
   This message appears because the app is not digitally signed yet, not because anything is wrong.

If Windows 11 **Smart App Control** is switched on, it blocks every unsigned app and won't offer "Run anyway".
You can switch it off in Windows Security > App & browser control > Smart App Control settings
(read the warning there first: on some Windows versions it can't be switched back on).

## Using it
Tick the items you want, set how long each stays on screen, reorder with the arrows, press **Start**
(it also starts by itself). Tick **Start with Windows** to launch it quietly at every login.
Settings are saved in `%AppData%\CoolerScreenControl`. A log file is written there too (`log.txt`).

## Build from source
Visual Studio 2022 with the ".NET desktop development" workload, open `CoolerScreenControl.csproj`
and press F5 (Visual Studio must run as administrator, because the app asks for it). Needs .NET 8.

Dependencies: [HidSharp](https://www.zer7.com/software/hidsharp) (USB), [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (CPU temperature).

## License
MIT - see `LICENSE`.
