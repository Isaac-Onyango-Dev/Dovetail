using System.IO;
using Path = System.IO.Path;
using Dovetail.Core;

namespace Dovetail.App;

/// <summary>
/// Everything the UI needs, with none of the UI in it. Owns the engine, the slot registry,
/// the game library and the settings, runs the pump, and raises plain events the tray and the
/// windows subscribe to.
///
/// Kept deliberately free of WPF and WinForms types so the whole of Stage 5's behaviour can
/// be driven from a console for testing, which is how every earlier stage was verified.
/// </summary>
public sealed class DovetailService : IDisposable
{
    private readonly string _profileDir;
    private DovetailEngine? _engine;
    private Thread? _pump;
    private volatile bool _stop;
    private DateTime _lastProfileStamp;
    private string _appliedGameKey = "";

    public SlotRegistry Slots { get; private set; }
    public GameLibrary Games { get; private set; }
    public DovetailSettings Settings { get; private set; }

    public string ProfileDirectory => _profileDir;
    public string SettingsPath => DovetailSettings.DefaultPath(_profileDir);
    public string GamesPath => GameLibrary.DefaultPath(_profileDir);

    public string BusStatus => _engine?.Bus.Status ?? "not started";
    public bool BusAvailable => _engine?.Bus.Available ?? false;
    public IReadOnlyList<PadChannel> Channels => _engine?.Channels ?? [];
    public GameProfile? ActiveGame { get; private set; }

    public event Action<string>? Log;
    public event Action<PadChannel>? ControllerConnected;
    public event Action<PadChannel>? ControllerDisconnected;
    public event Action<PadChannel>? UnrecognisedController;
    public event Action<GameProfile?>? ActiveGameChanged;
    public event Action? StateChanged;

    public DovetailService(string profileDirectory)
    {
        _profileDir = profileDirectory;
        Directory.CreateDirectory(_profileDir);

        foreach (var line in SlotRegistry.MigrateFromPortFiles(_profileDir)) Emit("migrated: " + line);

        Slots = SlotRegistry.Load(_profileDir);
        Games = GameLibrary.Load(GamesPath);
        Settings = DovetailSettings.Load(SettingsPath);
        foreach (var line in Slots.LoadLog) Emit(line);
    }

    private void Emit(string m) => Log?.Invoke(m);

    public bool Start()
    {
        _engine = new DovetailEngine(Slots, Emit);
        _engine.ControllerConnected += ch => { ControllerConnected?.Invoke(ch); StateChanged?.Invoke(); };
        _engine.ControllerDisconnected += ch => { ControllerDisconnected?.Invoke(ch); StateChanged?.Invoke(); };
        _engine.UnrecognisedController += ch => { UnrecognisedController?.Invoke(ch); StateChanged?.Invoke(); };

        bool ok = _engine.Start();
        _engine.Rescan();
        _lastProfileStamp = NewestProfileStamp();

        _stop = false;
        _pump = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "dovetail-pump",
            Priority = ThreadPriority.AboveNormal,
        };
        _pump.Start();
        return ok;
    }

    private DateTime NewestProfileStamp()
    {
        try
        {
            return Directory.GetFiles(_profileDir, "*.calibration.json")
                            .Select(File.GetLastWriteTimeUtc)
                            .DefaultIfEmpty(DateTime.MinValue).Max();
        }
        catch { return DateTime.MinValue; }
    }

    private void PumpLoop()
    {
        var nextRescan = DateTime.UtcNow;
        var nextProfileCheck = DateTime.UtcNow.AddSeconds(1);
        var nextGameCheck = DateTime.UtcNow.AddSeconds(1);

        while (!_stop)
        {
            try
            {
                if (DateTime.UtcNow >= nextRescan)
                {
                    _engine!.Rescan();
                    nextRescan = DateTime.UtcNow.AddSeconds(2);
                }

                if (DateTime.UtcNow >= nextProfileCheck)
                {
                    nextProfileCheck = DateTime.UtcNow.AddSeconds(1);
                    var stamp = NewestProfileStamp();
                    if (stamp != _lastProfileStamp)
                    {
                        _lastProfileStamp = stamp;
                        Thread.Sleep(120);
                        ReloadSlots();
                    }
                }

                // Per-game profiles, switched by watching the process list. Checked every
                // two seconds: a game takes far longer than that to reach a menu, and
                // polling faster would burn cycles during play for no benefit.
                if (DateTime.UtcNow >= nextGameCheck)
                {
                    nextGameCheck = DateTime.UtcNow.AddSeconds(2);
                    ApplyGameProfileIfChanged();
                }

                _engine!.Pump();
            }
            catch (Exception ex)
            {
                Emit($"pump error: {ex.Message}");
                Thread.Sleep(250);
            }
        }
    }

    public void ReloadSlots()
    {
        Slots = SlotRegistry.Load(_profileDir);
        _appliedGameKey = "";                 // force the game overrides to be re-applied
        _engine?.ReloadProfiles(Slots);
        ApplyGameProfileIfChanged();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Applies the running game's overrides on top of the slot calibrations, or removes them
    /// when no registered game is running. Overrides are applied to in-memory copies so the
    /// saved calibration files are never rewritten by a game switch; a game profile is a
    /// lens over a calibration, not an edit of it.
    /// </summary>
    private void ApplyGameProfileIfChanged()
    {
        var running = Games.FindRunning();
        string key = running?.ExecutableName ?? "";
        if (key == _appliedGameKey) return;
        _appliedGameKey = key;
        ActiveGame = running;

        var view = SlotRegistry.Load(_profileDir);
        if (running is not null)
        {
            foreach (var p in view.Slots.Values) running.ApplyTo(p);
            Emit($"game profile applied: \"{running.Name}\" ({running.ExecutableName})");
            Settings.LastGameApplied = running.Name;
        }
        else
        {
            Emit("no registered game running; slot calibrations in use unchanged");
            Settings.LastGameApplied = "";
        }
        SaveSettings();

        _engine?.ReloadProfiles(view);
        ActiveGameChanged?.Invoke(running);
        StateChanged?.Invoke();
    }

    public void SaveSettings() { try { Settings.Save(SettingsPath); } catch { } }
    public void SaveGames() { try { Games.Save(GamesPath); } catch { } }

    /// <summary>Section 7.4: forget a slot, which frees its virtual device immediately.</summary>
    public bool ForgetSlot(int slot)
    {
        bool had = Slots.Forget(slot);
        ReloadSlots();
        Emit(had ? $"slot {slot} forgotten" : $"slot {slot} had nothing to forget");
        return had;
    }

    public void RenameSlot(int slot, string name)
    {
        Slots.Rename(slot, name);
        ReloadSlots();
        Emit($"slot {slot} renamed to \"{name}\"");
    }

    public void Dispose()
    {
        _stop = true;
        _pump?.Join(1500);
        _engine?.Dispose();
        _engine = null;
    }
}
