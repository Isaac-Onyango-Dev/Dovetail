using System.IO;
using Path = System.IO.Path;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Dovetail.Core;
using MessageBox = System.Windows.MessageBox;

namespace Dovetail.App;

/// <summary>
/// The Section 7.1 first-run window. Shows the dependency step, the calibration step and
/// optional naming, and will not let setup be finished until the two things that actually
/// matter are true: the driver works, and at least one controller is calibrated.
///
/// It deliberately does not reimplement calibration. That wizard measures real hardware,
/// has been through five rounds of defect fixes against this pad, and its prompts are the
/// ones already proven. This window launches it and reacts to the result.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly DovetailService _service;
    private readonly ObservableCollection<SlotName> _names = [];

    public event Action? SetupFinished;

    public sealed class SlotName : INotifyPropertyChanged
    {
        public int Slot { get; init; }
        public string SlotLabel => $"Player {Slot}";
        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public FirstRunWindow(DovetailService service)
    {
        InitializeComponent();
        WindowPlacement.CentreAndFit(this, 720, 620);
        _service = service;
        NameList.ItemsSource = _names;
        RefreshDependencies();
        RefreshSlots();

        // Open showing step 1. Disabling the "Set up now" button when the driver is already
        // present moves keyboard focus to the next button, and the ScrollViewer dutifully
        // scrolls that into view, so the window would otherwise open half way down with the
        // first step's heading off screen.
        Loaded += (_, _) => Steps.ScrollToTop();
    }

    // ---------------------------------------------------------------- dependencies

    private void RefreshDependencies()
    {
        var mgr = new DependencyManager();
        var all = mgr.DetectAll();
        var required = all.Where(d => d.Required).ToList();

        DepStatus.Text = string.Join(Environment.NewLine, all.Select(d =>
            $"{(d.State == DependencyState.Installed ? "OK      " : d.Required ? "REQUIRED" : "optional")}  " +
            $"{d.DisplayName,-18} {d.Detail}"));

        // Both are required now, so "already installed" is not the same as "already set up".
        // HidHide that is installed but hiding nothing leaves a game free to bind to the raw
        // pad, which is the Stage 4 failure; the button has to stay live for that case.
        bool ready = required.All(d => d.State == DependencyState.Installed) && !NeedsConfiguring();
        DepSetupBtn.IsEnabled = !ready;
        DepSetupBtn.Content = ready ? "Already set up" : "Set up now";
        UpdateFinishState();
    }

    private void DepSetup_Click(object sender, RoutedEventArgs e)
    {
        string? engine = FindTool("dovetail-engine.exe");
        if (engine is null)
        {
            MessageBox.Show("dovetail-engine.exe could not be found next to Dovetail.",
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        DepSetupBtn.IsEnabled = false;
        DepStatus.Text = "Finding the current release, verifying its signature, then installing. " +
                         "Nothing to download by hand.";

        // Both drivers and the whole HidHide configuration in one elevated pass, so setting
        // Dovetail up is one action rather than three and never sends the user to a browser.
        // Installing HidHide without registering ourselves with it hides the pad from our own
        // engine; hiding nothing at all leaves a game free to bind to the raw pad. See 5.7.
        var psi = new ProcessStartInfo { FileName = engine, UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add("deps");
        psi.ArgumentList.Add("setup");
        try
        {
            var p = Process.Start(psi);
            if (p is null) { RefreshDependencies(); return; }
            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync();
                Dispatcher.Invoke(RefreshDependencies);
            });
        }
        catch (Exception ex)
        {
            DepStatus.Text = "Could not start the installer: " + ex.Message;
            DepSetupBtn.IsEnabled = true;
        }
    }

    private void DepRecheck_Click(object sender, RoutedEventArgs e) => RefreshDependencies();

    // ---------------------------------------------------------------- calibration

    private void RefreshSlots()
    {
        _service.ReloadSlots();
        var slots = _service.Slots;

        SlotStatus.Text = slots.CalibratedCount == 0
            ? "No controllers calibrated yet."
            : string.Join(Environment.NewLine, slots.Slots.OrderBy(k => k.Key).Select(kv =>
                $"Player {kv.Key}  \"{kv.Value.EffectiveName}\"  " +
                $"deadzone {kv.Value.Axes.GetValueOrDefault("leftStickX")?.Deadzone}, " +
                $"saturation {kv.Value.Axes.GetValueOrDefault("leftStickX")?.Saturation}"));

        _names.Clear();
        foreach (var (slot, prof) in slots.Slots.OrderBy(k => k.Key))
            _names.Add(new SlotName { Slot = slot, Name = prof.DisplayName });

        int next = slots.NextFreeSlotNumber();
        CalibrateBtn.Content = slots.CalibratedCount == 0
            ? "Calibrate a controller"
            : next > 0 ? $"Calibrate another (Player {next})" : "All four slots are in use";
        CalibrateBtn.IsEnabled = next > 0;

        UpdateFinishState();
    }

    private void Calibrate_Click(object sender, RoutedEventArgs e)
    {
        string? diag = FindTool("dovetail-diag.exe");
        if (diag is null)
        {
            MessageBox.Show("dovetail-diag.exe could not be found next to Dovetail.",
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var psi = new ProcessStartInfo { FileName = diag, UseShellExecute = true };
        psi.ArgumentList.Add("calibrate");
        psi.ArgumentList.Add("--dir");
        psi.ArgumentList.Add(_service.ProfileDirectory);

        try
        {
            var p = Process.Start(psi);
            if (p is null) return;
            CalibrateBtn.IsEnabled = false;
            SlotStatus.Text = "Calibration is running in its own window. Follow its prompts.";
            _ = Task.Run(async () =>
            {
                await p.WaitForExitAsync();
                Dispatcher.Invoke(RefreshSlots);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start calibration.\n\n" + ex.Message,
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SlotRefresh_Click(object sender, RoutedEventArgs e) => RefreshSlots();

    private void SaveNames_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in _names) _service.Slots.Rename(n.Slot, n.Name);
        _service.ReloadSlots();
        RefreshSlots();
    }

    // ---------------------------------------------------------------- finish

    /// <summary>
    /// HidHide installed but not actually hiding this pad. Reported separately from "missing",
    /// because it is the state that looks fine and fails in a game: Findings Log 4.1, where
    /// Sekiro bound to the raw pad through DirectInput and ignored every input Dovetail
    /// produced. Only asked once a controller is present; with nothing plugged in there is
    /// nothing to hide yet and nothing to complain about.
    /// </summary>
    private static bool NeedsConfiguring()
    {
        try
        {
            var pads = HidScan.Enumerate(DovetailEngine.DefaultVid, DovetailEngine.DefaultPid)
                              .Select(d => d.InstanceId)
                              .Where(s => s.Length > 0)
                              .ToList();
            return pads.Count > 0 && HidHideAccess.NeedsConfiguring(pads);
        }
        catch { return false; }
    }

    private void UpdateFinishState()
    {
        var mgr = new DependencyManager();
        bool depsOk = mgr.DetectAll().Where(d => d.Required)
                         .All(d => d.State == DependencyState.Installed);
        bool configured = !NeedsConfiguring();
        bool haveSlot = _service.Slots.CalibratedCount > 0;

        FinishBtn.IsEnabled = depsOk && configured && haveSlot;
        Blocker.Text = (depsOk, configured, haveSlot) switch
        {
            (false, _, _) => "The drivers still need setting up before Dovetail can do anything.",
            (true, false, _) => "HidHide is installed but is not hiding your controller yet, so a " +
                                "game can still bind to the raw pad and ignore Dovetail. Use Set up now.",
            (true, true, false) => "Calibrate at least one controller to finish.",
            _ => "Ready. Dovetail will move to the notification area.",
        };
    }

    private void Finish_Click(object sender, RoutedEventArgs e) => SetupFinished?.Invoke();

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
            catch { }
            dir = dir.Parent;
        }
        return null;
    }
}
