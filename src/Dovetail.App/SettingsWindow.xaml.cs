using System.IO;
using Path = System.IO.Path;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Dovetail.Core;
using MessageBox = System.Windows.MessageBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Dovetail.App;

/// <summary>
/// The settings window reached by left-clicking the tray icon, per Section 7.1. Covers the
/// four things the operator asked to be reachable: controllers with rename, re-calibrate and
/// forget; registered games; notification switches and auto-start; and dependency repair.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly DovetailService _service;
    private readonly ObservableCollection<SlotRow> _slots = [];
    private readonly ObservableCollection<GameProfile> _games = [];
    private bool _loading;

    public event Action<int>? CalibrateRequested;

    /// <summary>
    /// One controller card.
    ///
    /// The four stick-feel numbers are held here as editable state rather than read straight
    /// off the profile, so a slider can be dragged, seen, and either applied or reverted. They
    /// are written back through <see cref="StickFeel"/>, the same code the tune command uses,
    /// so the ranges cannot drift between the two ways of setting them.
    /// </summary>
    public sealed class SlotRow : INotifyPropertyChanged
    {
        public int Slot { get; init; }
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public string StateText { get; init; } = "";
        public Brush StateBrush { get; init; } = Brushes.Gray;

        private string _name = "";
        public string Name
        {
            get => _name;
            set { _name = value; Raise(nameof(Name)); }
        }

        // ---- slider bounds, from Core so the UI cannot offer a value the engine clamps ----
        public double DeadzoneMin => StickFeel.MinDeadzone;
        public double DeadzoneMax => StickFeel.MaxDeadzone;
        public double AntiDeadzoneMin => StickFeel.MinAntiDeadzone;
        public double AntiDeadzoneMax => StickFeel.MaxAntiDeadzone;
        public double SaturationMin => StickFeel.MinSaturation;
        public double SaturationMax => StickFeel.MaxSaturation;
        public double CurveMin => StickFeel.MinCurve;
        public double CurveMax => StickFeel.MaxCurve;

        public IReadOnlyList<StickFeel.Preset> Presets => StickFeel.Presets;

        /// <summary>
        /// Set while the row is being filled from the profile, so seeding the sliders does not
        /// look like an edit. Without it every card would open already marked dirty and Revert
        /// would be offered against changes nobody made.
        /// </summary>
        private bool _seeding;

        private bool _dirty;
        public bool Dirty
        {
            get => _dirty;
            private set { if (_dirty == value) return; _dirty = value; Raise(nameof(Dirty)); }
        }

        private double _deadzone;
        public double Deadzone
        {
            get => _deadzone;
            set => Set(ref _deadzone, Math.Round(value), nameof(Deadzone), nameof(DeadzoneText));
        }

        private double _antiDeadzone;
        public double AntiDeadzone
        {
            get => _antiDeadzone;
            set => Set(ref _antiDeadzone, Math.Round(value), nameof(AntiDeadzone), nameof(AntiDeadzoneText));
        }

        private double _saturation;
        public double Saturation
        {
            get => _saturation;
            set => Set(ref _saturation, Math.Round(value), nameof(Saturation), nameof(SaturationText));
        }

        private double _curve = 1.0;
        public double ResponseCurve
        {
            get => _curve;
            set => Set(ref _curve, Math.Round(value, 2), nameof(ResponseCurve), nameof(CurveText));
        }

        private string? _presetId;
        /// <summary>
        /// Choosing a preset moves the four sliders to its values and marks the card dirty. It
        /// does not write anything: the user still sees what the preset means before Apply.
        /// </summary>
        public string? PresetId
        {
            get => _presetId;
            set
            {
                if (_presetId == value) return;
                _presetId = value;
                Raise(nameof(PresetId));
                Raise(nameof(PresetSummary));

                if (_seeding || StickFeel.FindPreset(value) is not { } p) return;
                Deadzone = p.Deadzone;
                Saturation = p.Saturation;
                ResponseCurve = p.Curve;
                // The left stick's floor, because the card shows one anti-deadzone. Apply
                // writes each stick its own value when a preset is what produced these.
                AntiDeadzone = p.LeftFloor;
                Touch();
            }
        }

        public string PresetSummary =>
            StickFeel.FindPreset(_presetId) is { } p ? p.Summary : "custom values";

        public string DeadzoneText => $"{_deadzone:0} counts";
        public string AntiDeadzoneText => _antiDeadzone <= 0 ? "off" : $"{_antiDeadzone:0}";
        public string SaturationText =>
            _saturation <= 0 ? "mechanical" : $"{_saturation:0}  ({_saturation * 100 / StickFeel.MaxSaturation:0}%)";
        public string CurveText => _curve == 1.0 ? "1.00  linear" : $"{_curve:0.00}";

        private string _travelText = "";
        public string TravelText
        {
            get => _travelText;
            set { _travelText = value; Raise(nameof(TravelText)); }
        }

        private Brush _travelBrush = Brushes.Gray;
        public Brush TravelBrush
        {
            get => _travelBrush;
            set { _travelBrush = value; Raise(nameof(TravelBrush)); }
        }

        /// <summary>Fills the sliders from a profile without marking the card edited.</summary>
        public void Seed(CalibrationProfile.AxisCalibration axis)
        {
            _seeding = true;
            Deadzone = axis.Deadzone;
            AntiDeadzone = axis.AntiDeadzone;
            Saturation = axis.Saturation;
            ResponseCurve = axis.ResponseCurve;
            PresetId = MatchingPresetId(axis);
            _seeding = false;
            Dirty = false;
        }

        /// <summary>
        /// The preset these values came from, or null when they have been hand-tuned. Matching
        /// on the values rather than storing the chosen preset keeps the dropdown honest after
        /// a `tune` run from the command line, which knows nothing about this window.
        /// </summary>
        private static string? MatchingPresetId(CalibrationProfile.AxisCalibration a)
        {
            foreach (var p in StickFeel.Presets)
                if (a.Deadzone == p.Deadzone && a.Saturation == p.Saturation
                    && Math.Abs(a.ResponseCurve - p.Curve) < 0.001
                    && (a.AntiDeadzone == p.LeftFloor || a.AntiDeadzone == p.RightFloor))
                    return p.Id;
            return null;
        }

        public StickFeel.Settings ToSettings() => new()
        {
            Deadzone = (int)_deadzone,
            Saturation = (int)_saturation,
            ResponseCurve = _curve,
            // A preset knows each stick clears a different XInput threshold; a hand-set value
            // is one number the user chose, and goes to both. This mirrors the tune command,
            // where --preset splits the floors and --anti-deadzone does not.
            LeftAntiDeadzone = PresetFloor(false) ?? (int)_antiDeadzone,
            RightAntiDeadzone = PresetFloor(true) ?? (int)_antiDeadzone,
        };

        private int? PresetFloor(bool right) =>
            StickFeel.FindPreset(_presetId) is { } p && (int)_antiDeadzone == p.LeftFloor
                ? (right ? p.RightFloor : p.LeftFloor)
                : null;

        private void Touch() { if (!_seeding) Dirty = true; }

        /// <summary>
        /// Assigns, then raises for the property itself and for its derived label. Raising the
        /// property's own name matters when it is set in code rather than by the slider: Seed
        /// and a preset change both do that, and without the notification the sliders would
        /// stay where they were while the numbers beside them moved.
        /// </summary>
        private void Set(ref double field, double value, string name, string textName)
        {
            if (field.Equals(value)) return;
            field = value;
            Raise(name);
            Raise(textName);
            Touch();
        }

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public SettingsWindow(DovetailService service)
    {
        InitializeComponent();
        WindowPlacement.CentreAndFit(this, 900, 760);
        _service = service;
        SlotList.ItemsSource = _slots;
        GameList.ItemsSource = _games;
        RefreshFromService();
        RefreshDependencies();
    }

    public void RefreshFromService()
    {
        _loading = true;

        BusLine.Text = _service.BusAvailable
            ? $"virtual bus: {_service.BusStatus}   profiles: {_service.ProfileDirectory}"
            : $"VIRTUAL BUS UNAVAILABLE: {_service.BusStatus}";

        var live = _service.Channels.Where(c => c.Bound && c.Slot > 0).ToList();

        // Which cards the user is part way through editing. Rebuilding the list underneath an
        // unapplied slider would throw the edit away without saying so, so those rows keep
        // their values and only the rest are re-seeded.
        var dirty = _slots.Where(r => r.Dirty).ToDictionary(r => r.Slot);

        _slots.Clear();
        foreach (var (slot, prof) in _service.Slots.Slots.OrderBy(k => k.Key))
        {
            var ch = live.FirstOrDefault(c => c.Slot == slot);
            var ax = prof.Axes.GetValueOrDefault("leftStickX");
            var row = new SlotRow
            {
                Slot = slot,
                Title = $"Player {slot}  -  {prof.EffectiveName}",
                Name = prof.DisplayName,
                StateText = ch is null ? "not connected" : $"connected, XInput slot {ch.Virtual?.UserIndex}",
                StateBrush = ch is null ? Brushes.Gray : (Brush)FindResource("Good"),
                Detail =
                    $"centre {ax?.Centre}   range {ax?.Min}..{ax?.Max}   " +
                    $"triggers {(prof.Triggers.DigitalOnly ? "digital" : "analog")}",
            };

            if (ax is not null) row.Seed(ax);
            if (dirty.TryGetValue(slot, out var pending)) CarryPendingEdit(pending, row);
            SetTravel(row, ax);
            _slots.Add(row);
        }
        AddControllerBtn.IsEnabled = _service.Slots.NextFreeSlotNumber() > 0;

        _games.Clear();
        foreach (var g in _service.Games.Games) _games.Add(g);
        ActiveGameLine.Text = _service.ActiveGame is { } act
            ? $"active now: \"{act.Name}\" ({act.ExecutableName})"
            : "no registered game is running; slot calibrations are in use unchanged";

        NotifyConnect.IsChecked = _service.Settings.ShowConnectNotifications;
        NotifyDisconnect.IsChecked = _service.Settings.ShowDisconnectNotifications;
        NotifyUnrecognised.IsChecked = _service.Settings.ShowUnrecognisedNotifications;

        AutoStartBox.IsChecked = AutoStart.IsEnabled();
        AutoStartDetail.Text = AutoStart.CurrentCommand() is { } cmd
            ? "registry entry: " + cmd
            : "no auto-start entry is present";

        _loading = false;
    }

    /// <summary>Moves an unapplied edit onto the freshly built row, so a refresh cannot eat it.</summary>
    private static void CarryPendingEdit(SlotRow from, SlotRow to)
    {
        to.PresetId = from.PresetId;
        to.Deadzone = from.Deadzone;
        to.AntiDeadzone = from.AntiDeadzone;
        to.Saturation = from.Saturation;
        to.ResponseCurve = from.ResponseCurve;
    }

    /// <summary>
    /// How much of a stick push the game will actually receive, given these four numbers.
    ///
    /// This is the point of the whole card, so it is stated rather than left to be inferred.
    /// It is computed by pushing real raw values through the same scaler the engine uses, so
    /// the figure and the game cannot disagree; the colour is the quick read, the number is
    /// the one to compare between settings.
    /// </summary>
    private void SetTravel(SlotRow row, CalibrationProfile.AxisCalibration? axis)
    {
        if (axis is null || axis.Max <= axis.Centre)
        {
            row.TravelText = "not calibrated yet";
            row.TravelBrush = Brushes.Gray;
            return;
        }

        int pct = StickFeel.UsableTravelPercent(axis, rightStick: false);
        row.TravelText = $"{pct}% of your push reaches the game";
        row.TravelBrush = pct switch
        {
            >= 90 => (Brush)FindResource("Good"),
            >= 70 => (Brush)FindResource("Warn"),
            _ => (Brush)FindResource("Accent"),
        };
    }

    // ---------------------------------------------------------------- controllers

    /// <summary>
    /// Writes the card's four numbers into the slot's profile.
    ///
    /// Through <see cref="StickFeel.Apply"/>, which is what the tune command also calls, so the
    /// clamping is one implementation. The engine hot-reloads a changed profile within a
    /// second, which is why there is no restart prompt: the change can be felt in a game that
    /// is already running.
    /// </summary>
    private void ApplyFeel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: int slot }) return;
        var row = _slots.FirstOrDefault(r => r.Slot == slot);
        if (row is null) return;

        string path = SlotRegistry.PathForSlot(_service.ProfileDirectory, slot);
        try
        {
            // Loaded from disk rather than edited in memory: the in-memory copy may be carrying
            // a running game's overrides, and writing those back would bake a temporary lens
            // into the saved calibration.
            var profile = CalibrationProfile.Load(path);
            StickFeel.Apply(profile, row.ToSettings());
            StickFeel.Note(profile, row.PresetId, "settings window");
            profile.Save(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The stick settings could not be saved." + Environment.NewLine + Environment.NewLine +
                ex.Message,
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _service.ReloadSlots();
        RefreshFromService();
    }

    private void RevertFeel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: int slot }) return;
        var row = _slots.FirstOrDefault(r => r.Slot == slot);
        var axis = _service.Slots.ForSlot(slot)?.Axes.GetValueOrDefault("leftStickX");
        if (row is null || axis is null) return;

        row.Seed(axis);
        SetTravel(row, axis);
    }

    private void SaveName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b || b.Tag is not int slot) return;
        var row = _slots.FirstOrDefault(r => r.Slot == slot);
        if (row is null) return;
        _service.RenameSlot(slot, row.Name);
        RefreshFromService();
    }

    private void Recalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: int slot })
            CalibrateRequested?.Invoke(slot);
    }

    private void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: int slot }) return;
        var prof = _service.Slots.ForSlot(slot);
        if (prof is null) return;

        // Section 7.4: confirm, because this costs a full recalibration to undo.
        var answer = MessageBox.Show(
            $"Forget \"{prof.EffectiveName}\"?\n\n" +
            "Its calibration will be deleted and its virtual controller released immediately.\n\n" +
            "If that controller connects again it is treated as brand new and has to be set " +
            "up from scratch, which means redoing the full calibration sweep: rest state, " +
            "both stick ranges, both triggers, and the stick-click tests.",
            "Dovetail - forget controller",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _service.ForgetSlot(slot);
        RefreshFromService();
    }

    private void AddController_Click(object sender, RoutedEventArgs e)
    {
        int next = _service.Slots.NextFreeSlotNumber();
        if (next > 0) CalibrateRequested?.Invoke(next);
    }

    // ---------------------------------------------------------------- games

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pick the game's executable",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        var g = _service.Games.Add(dlg.FileName);
        // Seed the overrides from the current calibration so the entry starts as a no-op
        // rather than silently changing feel the moment it is registered.
        var seed = _service.Slots.Slots.Values.FirstOrDefault()?.Axes.GetValueOrDefault("leftStickX");
        if (seed is not null)
        {
            g.Deadzone ??= seed.Deadzone;
            g.AntiDeadzone ??= seed.AntiDeadzone;
            g.Saturation ??= seed.Saturation;
            g.ResponseCurve ??= seed.ResponseCurve;
        }
        _service.SaveGames();
        _service.ReloadSlots();
        RefreshFromService();

        MessageBox.Show(
            $"\"{g.Name}\" registered.\n\n" +
            "Its settings currently match your controller calibration, so nothing changes " +
            "yet. To give it its own deadzone, saturation or curve, edit:\n\n" +
            $"{_service.GamesPath}\n\n" +
            "Dovetail picks the change up within a second.\n\n" +
            "Dovetail will not launch this game. Start it from its own shortcut as usual.",
            "Dovetail - game registered", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string exe }) return;
        if (MessageBox.Show($"Stop applying a profile for {exe}?", "Dovetail",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _service.Games.Remove(exe);
        _service.SaveGames();
        _service.ReloadSlots();
        RefreshFromService();
    }

    // ---------------------------------------------------------------- notifications

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = AutoStartBox.IsChecked == true;
        if (want)
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0 || !AutoStart.Enable(exe))
                MessageBox.Show("Dovetail could not write the auto-start entry.",
                    "Dovetail", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else AutoStart.Disable();

        _service.Settings.AutoStartOnLogin = want;
        _service.SaveSettings();
        RefreshFromService();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Notification switches are saved on close rather than on every click, so a run of
        // toggles is one write.
        _service.Settings.ShowConnectNotifications = NotifyConnect.IsChecked == true;
        _service.Settings.ShowDisconnectNotifications = NotifyDisconnect.IsChecked == true;
        _service.Settings.ShowUnrecognisedNotifications = NotifyUnrecognised.IsChecked == true;
        _service.SaveSettings();
        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- dependencies

    private void RefreshDependencies()
    {
        var all = new DependencyManager().DetectAll();
        DepText.Text = string.Join(Environment.NewLine + Environment.NewLine, all.Select(d =>
            $"{d.DisplayName}\n" +
            $"  state          : {(d.State == DependencyState.Installed ? "OK" : d.State.ToString())}" +
            $"{(d.Required ? "  (required)" : "  (optional)")}\n" +
            $"  driver version : {d.DriverVersion ?? "(none)"}\n" +
            $"  package        : {d.PackageVersion ?? "(not in Add/Remove Programs)"}\n" +
            $"  detail         : {d.Detail}"));
    }

    private void DepRecheck_Click(object sender, RoutedEventArgs e) => RefreshDependencies();

    /// <summary>
    /// Replays the first-launch animation. It is here because it is deliberately unreachable
    /// anywhere else: the intro's whole contract is that it plays once, so the settings flag
    /// stays set and this opens the window directly rather than clearing the flag.
    /// </summary>
    private void ReplayIntro_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var intro = new IntroWindow { Owner = this };
            intro.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The intro artwork could not be loaded." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Dovetail", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DepRepair_Click(object sender, RoutedEventArgs e)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? engine = null;
        while (dir is not null && engine is null)
        {
            var direct = Path.Combine(dir.FullName, "dovetail-engine.exe");
            if (File.Exists(direct)) { engine = direct; break; }
            try { engine = dir.GetFiles("dovetail-engine.exe", SearchOption.AllDirectories).FirstOrDefault()?.FullName; }
            catch { }
            dir = dir.Parent;
        }
        if (engine is null)
        {
            MessageBox.Show("dovetail-engine.exe could not be found.", "Dovetail",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Requirement 4: one Repair covers everything. Elevated, because reinstalling a driver
        // and editing HidHide's configuration both need it, and two prompts for one button is
        // worse than one. This ran unelevated as "deps repair all" and could therefore fix a
        // missing driver but never a HidHide problem, which is the more common complaint.
        var psi = new ProcessStartInfo { FileName = engine, UseShellExecute = true, Verb = "runas" };
        psi.ArgumentList.Add("deps");
        psi.ArgumentList.Add("repair-and-allow");
        try
        {
            var p = Process.Start(psi);
            if (p is not null)
                _ = Task.Run(async () => { await p.WaitForExitAsync(); Dispatcher.Invoke(RefreshDependencies); });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start repair.\n\n" + ex.Message, "Dovetail",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
