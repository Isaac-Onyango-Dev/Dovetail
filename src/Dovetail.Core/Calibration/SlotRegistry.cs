namespace Dovetail.Core;

/// <summary>
/// The calibrated player slots Dovetail knows about, and the rules for handing them out.
///
/// Replaces the earlier port-keyed <see cref="ProfileSet"/>. Findings Log 7.6 settled the
/// identity question: there is no per-unit identity available, because both pads report
/// identical vendor id, product id, revision and product string, neither reports a serial
/// number, and the receiver assigns a channel by power-on order. So Dovetail behaves the way a
/// console does. Slot 1 is whoever connects first. The calibration and the name belong to
/// the slot.
///
/// The second rule, from Section 7.2, is enforced here rather than in the UI: **a controller
/// with no calibrated slot to claim gets no virtual device.** It is announced to the user and
/// otherwise left alone. Silently translating an unrecognised pad with another slot's numbers
/// is the exact failure Stage 4 found, where a pad was played for hours on the other unit's
/// calibration without anyone noticing.
/// </summary>
public sealed class SlotRegistry
{
    public const int MaxSlots = 4;

    private readonly Dictionary<int, CalibrationProfile> _slots = [];

    public string Directory { get; }
    public List<string> LoadLog { get; } = [];

    public IReadOnlyDictionary<int, CalibrationProfile> Slots => _slots;
    public int CalibratedCount => _slots.Count;
    public bool AnyCalibrated => _slots.Count > 0;

    private SlotRegistry(string directory) => Directory = directory;

    public static string FileNameForSlot(int slot) => $"slot{slot}.calibration.json";
    public static string PathForSlot(string directory, int slot) =>
        Path.Combine(directory, FileNameForSlot(slot));

    public static SlotRegistry Load(string directory)
    {
        var reg = new SlotRegistry(directory);
        if (!System.IO.Directory.Exists(directory))
        {
            reg.LoadLog.Add($"profile directory not found: {directory}");
            return reg;
        }

        for (int slot = 1; slot <= MaxSlots; slot++)
        {
            string path = PathForSlot(directory, slot);
            if (!File.Exists(path)) continue;
            try
            {
                var p = CalibrationProfile.Load(path);
                p.SlotNumber = slot;               // the filename is authoritative
                reg._slots[slot] = p;
                reg.LoadLog.Add($"slot {slot}: \"{p.EffectiveName}\"");
            }
            catch (Exception ex)
            {
                reg.LoadLog.Add($"slot {slot}: unreadable, {ex.Message}");
            }
        }

        if (reg._slots.Count == 0)
            reg.LoadLog.Add("no calibrated slots; the next controller seen will be announced as new");
        return reg;
    }

    public CalibrationProfile? ForSlot(int slot) => _slots.GetValueOrDefault(slot);

    /// <summary>
    /// Lowest calibrated slot not in <paramref name="taken"/>, or null when every calibrated
    /// slot is already in use. Null is the signal that a controller is unrecognised, and it
    /// covers both cases that matter: nothing is calibrated yet, and more controllers are
    /// connected than have been set up.
    /// </summary>
    public int? ClaimNextSlot(IEnumerable<int> taken)
    {
        var used = taken.ToHashSet();
        for (int slot = 1; slot <= MaxSlots; slot++)
            if (_slots.ContainsKey(slot) && !used.Contains(slot))
                return slot;
        return null;
    }

    /// <summary>Lowest slot number with no profile, for a new calibration to write into.</summary>
    public int NextFreeSlotNumber()
    {
        for (int slot = 1; slot <= MaxSlots; slot++)
            if (!_slots.ContainsKey(slot)) return slot;
        return 0;
    }

    /// <summary>
    /// Deletes a slot's calibration, per Section 7.4. A controller that later claims this
    /// slot number is treated as brand new, because the file it would have loaded is gone.
    /// </summary>
    public bool Forget(int slot)
    {
        string path = PathForSlot(Directory, slot);
        bool had = _slots.Remove(slot);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { LoadLog.Add($"slot {slot}: could not delete {path}, {ex.Message}"); return false; }
        return had;
    }

    public void Rename(int slot, string name)
    {
        if (!_slots.TryGetValue(slot, out var p)) return;
        p.DisplayName = name?.Trim() ?? "";
        p.Save(PathForSlot(Directory, slot));
    }

    /// <summary>
    /// One-time migration from the port-keyed naming this project used earlier. Keeps the
    /// measured values rather than asking the operator to recalibrate, and records that the
    /// slot number came from a port number.
    ///
    /// The old file is moved into _superseded once its data is safely in the slot file, so
    /// this really does run once. Leaving it in place made the migration re-announce itself
    /// on every single launch and left a file in the profiles folder that looked live but
    /// was not read by anything.
    /// </summary>
    public static List<string> MigrateFromPortFiles(string directory)
    {
        var log = new List<string>();
        if (!System.IO.Directory.Exists(directory)) return log;

        foreach (var old in System.IO.Directory.GetFiles(directory, "pad-port*.calibration.json")
                                               .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(old);
            var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int port) || port < 1 || port > MaxSlots) continue;

            string target = PathForSlot(directory, port);
            if (File.Exists(target))
            {
                // The slot file wins: it is the one the app reads. The old file is retired
                // rather than deleted, because it holds measurements taken from hardware.
                if (Retire(directory, old, out string why))
                    log.Add($"{name}: slot {port} already calibrated, old file retired to _superseded");
                else
                    log.Add($"{name}: slot {port} already calibrated, old file left in place ({why})");
                continue;
            }

            try
            {
                var p = CalibrationProfile.Load(old);
                p.SlotNumber = port;
                p.Name = $"slot{port}";
                if (string.IsNullOrWhiteSpace(p.DisplayName))
                    p.DisplayName = $"Player {port} Pad";
                p.Notes.Insert(0,
                    $"Migrated from {name} on {DateTime.Now:yyyy-MM-dd}. The slot number came from the " +
                    "receiver port this pad happened to be on. Identity is now by player slot, not by " +
                    "port or by unit; see Findings Log 7.6.");
                p.Save(target);

                // Retire only after the new file is written, so a failure at any point
                // leaves the original data still readable where it was.
                string suffix = Retire(directory, old, out string why)
                    ? ", old file retired to _superseded"
                    : $", old file left in place ({why})";
                log.Add($"{name} -> {Path.GetFileName(target)}  named \"{p.EffectiveName}\"{suffix}");
            }
            catch (Exception ex) { log.Add($"{name}: migration failed, {ex.Message}"); }
        }
        return log;
    }

    /// <summary>
    /// Moves a superseded profile out of the way into a _superseded subfolder, which is the
    /// convention this project already uses for data that is no longer read but is not
    /// throwaway. Returns false with a reason rather than throwing, because failing to tidy
    /// up must never stop the app starting.
    /// </summary>
    private static bool Retire(string directory, string file, out string reason)
    {
        try
        {
            string bin = Path.Combine(directory, "_superseded");
            System.IO.Directory.CreateDirectory(bin);
            string dest = Path.Combine(bin, Path.GetFileName(file));
            if (File.Exists(dest))
                dest = Path.Combine(bin,
                    $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(file)}");
            File.Move(file, dest);
            reason = "";
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }
}
