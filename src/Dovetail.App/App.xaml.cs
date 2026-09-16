using System.IO;
using Path = System.IO.Path;
using System.Diagnostics;
using System.Windows;
using Dovetail.Core;
using MessageBox = System.Windows.MessageBox;

namespace Dovetail.App;

/// <summary>
/// Application entry point and the owner of the Section 7.1 run-mode flow:
///
///   first launch  ->  full window through setup and calibration
///                 ->  one-time handover dialog
///   every launch after that, including auto-start  ->  tray only
///
/// The handover dialog is the part that is easy to leave out and the part the operator
/// specifically asked for. Without it, a user who saw a window the first time has no idea
/// where it went on the second launch.
/// </summary>
public partial class App : System.Windows.Application
{
    private DovetailService? _service;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private static Mutex? _single;
    private static string? _profileDir;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Installed before anything else can fail. Without this, a fault on the UI thread
        // ends the process with nothing but a Windows error dialog and an event-log entry,
        // which is no use to someone who just wants their controller to work. It cost real
        // time during Stage 5: an InvariantGlobalization setting that WPF cannot tolerate
        // showed up only as exit code 0xE0434352.
        DispatcherUnhandledException += (_, ex) => { ReportCrash("UI thread", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => ReportCrash("background thread", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { ReportCrash("task", ex.Exception); ex.SetObserved(); };

        // One instance only. Two copies of Dovetail would each create virtual pads for the
        // same physical controllers, which is exactly the duplicate-device problem
        // Section 8.1 asks us not to create.
        _single = new Mutex(true, @"Local\DovetailGamepadEmulator", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "Dovetail is already running. Look for it in the notification area.",
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        string profileDir = ResolveProfileDirectory();
        _service = new DovetailService(profileDir);
        _service.Log += m => Debug.WriteLine("[dovetail] " + m);

        _tray = new TrayIcon(_service);
        _tray.OpenSettingsRequested += ShowSettings;
        _tray.RunCalibrationRequested += () => LaunchCalibration(null);
        _tray.CalibrateSlotRequested += slot => LaunchCalibration(slot);
        _tray.RepairDependenciesRequested += RepairDependencies;
        _tray.ExitRequested += () => Shutdown();

        _service.ControllerConnected += ch => Post(() => { _tray!.NotifyConnected(ch); _tray.Refresh(); });
        _service.ControllerDisconnected += ch => Post(() => { _tray!.NotifyDisconnected(ch); _tray.Refresh(); });
        _service.UnrecognisedController += ch => Post(() => { _tray!.NotifyUnrecognised(ch); _tray.Refresh(); });
        _service.StateChanged += () => Post(() => { _tray!.Refresh(); _settings?.RefreshFromService(); });

        if (!_service.Start())
        {
            // The bus is the one hard dependency. Say so plainly rather than appearing to
            // work and translating nothing.
            Post(() => _tray!.ShowMessage(
                "Dovetail cannot create a virtual controller",
                _service.BusStatus + "  Use Repair dependencies from the tray menu."));
        }

        bool startedByAutoStart = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        // --settings opens straight into settings. Stage 6 puts this behind a Start Menu
        // entry, so the window is reachable without hunting for the tray icon, which is the
        // one part of a tray-only app that people cannot find.
        bool openSettings = e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));

        // --intro replays the first-launch animation without disturbing anything else. It is
        // what the settings window's "Play the intro again" uses, and it is the only way to
        // look at the animation without deleting settings.json.
        if (e.Args.Any(a => a.Equals("--intro", StringComparison.OrdinalIgnoreCase)))
        {
            var replay = new IntroWindow();
            replay.Finished += () => { _tray!.Refresh(); if (openSettings) ShowSettings(); };
            replay.Show();
            return;
        }

        // --check opens the controller check on its own, for a pad that is already set up.
        // --slot picks which player, --only re-tests named inputs rather than all twenty-seven:
        //     Dovetail.exe --check --slot 1 --only face_down,r2
        if (e.Args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase)))
        {
            int? which = int.TryParse(ArgValue(e.Args, "--slot"), out int s) && s >= 1 ? s : null;
            LaunchCalibration(which, ArgValue(e.Args, "--only"));
            return;
        }

        if (!_service.Settings.FirstRunCompleted && !startedByAutoStart)
            ShowIntroThenFirstRun();
        else
        {
            _tray.Refresh();
            if (openSettings) ShowSettings();
        }
    }

    /// <summary>
    /// Plays the intro once, then opens setup. Never on a tray-mode start, and never twice.
    ///
    /// The flag is written before the animation runs rather than after. Written afterwards, a
    /// crash or a kill part way through would leave it false and the intro would greet the
    /// operator again on every launch until it happened to finish, which is the worst possible
    /// failure mode for a thing whose entire contract is "once".
    /// </summary>
    private void ShowIntroThenFirstRun()
    {
        if (_service!.Settings.IntroPlayed || !PadArtwork.Available())
        {
            ShowFirstRun();
            return;
        }

        _service.Settings.IntroPlayed = true;
        _service.SaveSettings();

        try
        {
            var intro = new IntroWindow();
            intro.Finished += ShowFirstRun;
            intro.Show();
        }
        catch (Exception ex)
        {
            // An intro is decoration. If the artwork will not load, setup still has to open.
            Debug.WriteLine("[dovetail] intro failed: " + ex);
            ShowFirstRun();
        }
    }

    /// <summary>
    /// Writes the fault to a log beside the profiles and tells the operator where it went.
    /// A tray app that dies in silence is indistinguishable from one that is working, so the
    /// message matters as much as the log.
    /// </summary>
    private void ReportCrash(string where, Exception? ex)
    {
        string text =
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Dovetail {typeof(App).Assembly.GetName().Version}  " +
            $"fault on the {where}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 78)}{Environment.NewLine}";

        string logPath = "";
        try
        {
            logPath = Path.Combine(ResolveProfileDirectory(), "dovetail-errors.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, text);
        }
        catch { /* if even the log cannot be written, the dialog below is all there is */ }

        Debug.WriteLine("[dovetail] " + text);
        try
        {
            MessageBox.Show(
                "Dovetail hit a problem and has stopped part of what it was doing." +
                Environment.NewLine + Environment.NewLine +
                (ex?.Message ?? "No detail was available.") +
                Environment.NewLine + Environment.NewLine +
                (logPath.Length > 0 ? "The full detail was written to:" + Environment.NewLine + logPath
                                    : "The detail could not be written to a file.") +
                Environment.NewLine + Environment.NewLine +
                "Controller translation may have stopped. Restarting Dovetail from its tray " +
                "icon, or from the Start menu, will start it again.",
                "Dovetail - something went wrong",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    /// <summary>
    /// %LOCALAPPDATA%\Dovetail\profiles, migrating on first run. See <see cref="ProfileStore"/>
    /// for why it is no longer the folder beside the executable. Cached because the crash
    /// handler calls it too, and a fault during startup must not re-enter the migration.
    /// </summary>
    private static string ResolveProfileDirectory() =>
        _profileDir ??= ProfileStore.Resolve(m => Debug.WriteLine("[dovetail] " + m));

    private void Post(Action a) => Dispatcher.BeginInvoke(a);

    /// <summary>The value following a named switch, or null. Same shape as the console tools'.</summary>
    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void ShowFirstRun()
    {
        var w = new FirstRunWindow(_service!);
        w.SetupFinished += () =>
        {
            _service!.Settings.FirstRunCompleted = true;
            _service.SaveSettings();

            // Section 7.1: the one-time handover, shown exactly once.
            if (!_service.Settings.HandoverExplained)
            {
                _service.Settings.HandoverExplained = true;
                _service.SaveSettings();

                // Owned by the setup window. Without an owner these are free-floating
                // top-level windows that Windows will leave sitting behind a browser, so
                // the one message that says where the app went can be missed entirely.
                MessageBox.Show(w,
                    "Setup complete.\n\n" +
                    "Dovetail will now run in the background - look for it in the notification " +
                    "area, near the clock.\n\n" +
                    "Click that icon any time to change settings, re-run calibration, or " +
                    "rename a controller.",
                    "Dovetail - setup complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }

            AskAboutAutoStart(w);
            w.Close();
            _tray!.Refresh();
        };
        w.Show();
    }

    /// <summary>Section 7.5: auto-start is wanted, but asked once rather than assumed.</summary>
    private void AskAboutAutoStart(Window? owner)
    {
        if (_service!.Settings.AutoStartPromptShown) return;
        _service.Settings.AutoStartPromptShown = true;

        string question =
            "Start Dovetail automatically when you sign in to Windows?\n\n" +
            "Recommended. Dovetail needs to be running before a game starts, because games " +
            "check for controllers when they launch. With auto-start on, you can open games " +
            "from their normal shortcuts and they will just find a controller.\n\n" +
            "You can change this later from the tray icon.";
        const string title = "Dovetail - start with Windows?";

        var answer = owner is not null
            ? MessageBox.Show(owner, question, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

        bool wanted = answer == MessageBoxResult.Yes;
        _service.Settings.AutoStartOnLogin = wanted;
        if (wanted)
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0 && !AutoStart.Enable(exe))
                MessageBox.Show("Dovetail could not write the auto-start entry.",
                    "Dovetail - auto-start", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            AutoStart.Disable();
        }
        _service.SaveSettings();
    }

    private void ShowSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_service!);
        _settings.CalibrateRequested += slot => LaunchCalibration(slot);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    /// <summary>
    /// Controller setup, in two parts.
    ///
    /// The first is <see cref="CalibrationWindow"/>: a picture of the pad, one named input at a
    /// time, the right part of it lit up, advancing when the pad reports the press. That window
    /// is a front end over <see cref="InputSweep"/>, which is the Stage 1 detection moved into
    /// Dovetail.Core so the graphical check and the console wizard judge a press identically.
    ///
    /// The second is the measurement, and it stays in the console wizard. That tool produced
    /// every calibration in this project, has been through five rounds of defect fixes against
    /// real hardware, and its step ordering is the thing that made calibration work at all.
    /// Rewriting it as a WPF screen would discard that and reintroduce the same class of bugs.
    /// The window is the front door; the measurement stays where it was proven.
    /// </summary>
    /// <param name="only">
    /// Step ids to re-test instead of the whole script, as --only takes. The check screen also
    /// offers this per input once a run has finished; this parameter is how the command line
    /// reaches the same thing.
    /// </param>
    private void LaunchCalibration(int? slot, string? only = null)
    {
        int target = slot ?? (_service!.Slots.NextFreeSlotNumber() is int free and > 0 ? free : 1);

        if (!PadArtwork.Available())
        {
            LaunchMeasurement(slot);
            return;
        }

        // The slot's saved profile, when it has one. Its recorded report id lets the check pin
        // the receiver port without asking for an identifying press, which is what the console
        // wizard does on a re-run.
        var saved = _service!.Slots.ForSlot(target);

        var check = new CalibrationWindow(target, saved, only);
        check.ContinueToMeasurement += () => LaunchMeasurement(slot);
        check.Show();
    }

    /// <summary>Stage 2, unchanged: the console wizard that measures rest, travel and triggers.</summary>
    private void LaunchMeasurement(int? slot)
    {
        string? diag = FindTool("dovetail-diag.exe");
        if (diag is null)
        {
            MessageBox.Show(
                "The calibration tool (dovetail-diag.exe) could not be found next to Dovetail.",
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = diag,
            UseShellExecute = true,
        };
        psi.ArgumentList.Add("calibrate");
        psi.ArgumentList.Add("--dir");
        psi.ArgumentList.Add(_service!.ProfileDirectory);
        if (slot is int s) { psi.ArgumentList.Add("--slot"); psi.ArgumentList.Add(s.ToString()); }

        try
        {
            var p = Process.Start(psi);
            if (p is not null)
            {
                // Reload when the wizard exits so the new calibration takes effect without a
                // restart, which is the same hot-reload path the engine already uses.
                _ = Task.Run(async () =>
                {
                    await p.WaitForExitAsync();
                    Post(() => _service!.ReloadSlots());
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the calibration tool.\n\n" + ex.Message,
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RepairDependencies()
    {
        string? engine = FindTool("dovetail-engine.exe");
        if (engine is null)
        {
            MessageBox.Show("dovetail-engine.exe could not be found next to Dovetail.",
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // "repair all" reinstalls the drivers; "access" puts this installation back on
        // HidHide's allow list. The second is the one that fixes a pad that is present, healthy
        // and invisible, which is what happens to every copy that is not the one the list was
        // written for. Both run in the elevated helper, one elevation prompt for the pair.
        var psi = new ProcessStartInfo { FileName = engine, UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add("deps");
        psi.ArgumentList.Add("repair-and-allow");
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start dependency repair.\n\n" + ex.Message,
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? FindTool(string exeName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var direct = Path.Combine(dir.FullName, exeName);
            if (File.Exists(direct)) return direct;
            try
            {
                var found = dir.GetFiles(exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null) return found.FullName;
            }
            catch { /* unreadable subtree, keep walking up */ }
            dir = dir.Parent;
        }
        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _service?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
