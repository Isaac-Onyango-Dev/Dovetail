using System.Text.Json;

namespace Dovetail.Core;

/// <summary>
/// Everything an uninstaller has to undo, and nothing else.
///
/// **What this removes.** Exactly three things, each one written by Dovetail and by nothing
/// else: the auto-start value in the current user's Run key, Dovetail's own entries on
/// HidHide's application allow list, and - only when the user says so - the calibration
/// profiles in <see cref="ProfileStore.CanonicalDirectory"/>.
///
/// **What this deliberately leaves alone, and why.**
///
/// <list type="bullet">
/// <item><b>ViGEmBus and HidHide stay installed.</b> They are separately installed, separately
/// listed in Add/Remove Programs products, and other software on the machine uses them:
/// DS4Windows, Steam and x360ce all drive ViGEmBus, and this project's own Stage 0 audit found
/// x360ce already present on the development machine. Uninstalling a kernel driver because one
/// of its users went away would break the others silently. The uninstaller says they were left
/// and where to remove them from if the user wants to.</item>
///
/// <item><b>The rest of the Run key.</b> The value is deleted by name. Deleting the key would
/// take every other program's startup entry with it.</item>
///
/// <item><b>Allow-list entries that are not ours.</b> The list belongs to the operator and
/// commonly names DS4Windows or Steam. Only entries whose file name is in
/// <see cref="HidHideAccess.AllOwnNames"/> are touched, which is the three current
/// executables plus the three pre-rename ones.</item>
///
/// <item><b>Any HidHide setting Dovetail did not make.</b> A device the user had already
/// hidden stays hidden; a cloak that was already on stays on. What setup itself changed is
/// recorded in <see cref="HidHideState"/> and reversed here, and only that.
///
/// Leaving even our own changes behind is the reading that sounds safest and is the harmful
/// one: cloaking stays on, the pad stays hidden, and our allow-list entries have just been
/// removed, so the user's controller is invisible to everything that is not DS4Windows or
/// HidHide's own client. The pad appears broken and nothing on the machine explains why.
/// Cloaking is only turned back off when Dovetail turned it on AND nothing else is left
/// relying on it.</item>
/// </list>
///
/// **Reversible.** Every run writes a restore point to <see cref="RestoreDirectory"/> naming
/// the exact registry value and allow-list paths removed, and profiles are moved into that
/// same folder rather than deleted. <see cref="Restore"/> puts all of it back. This is the
/// same habit as the project's _rollback folder, applied to the one operation that otherwise
/// destroys measured data the user cannot recreate without a full sweep per pad.
/// </summary>
public sealed class UninstallCleanup
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>
    /// %LOCALAPPDATA%\Dovetail\uninstall. Never removed by a cleanup, so the restore point and
    /// any moved profiles always survive the thing that created them.
    /// </summary>
    public static string DefaultRestoreDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return local.Length == 0
                ? Path.Combine(Path.GetTempPath(), ProfileStore.ProductFolder, "uninstall")
                : Path.Combine(local, ProfileStore.ProductFolder, "uninstall");
        }
    }

    /// <summary>The profiles folder this instance would remove.</summary>
    public string ProfileDirectory { get; }

    /// <summary>Where this instance writes restore points and moves profiles to.</summary>
    public string RestoreDirectory { get; }

    /// <summary>
    /// Both folders are constructor arguments rather than reads of a static, so the thing that
    /// deletes a user's calibrations can be pointed somewhere else and exercised for real.
    /// Production callers pass nothing and get the live folders.
    /// </summary>
    private readonly string? _hidHideStatePath;

    public UninstallCleanup(string? profileDirectory = null, string? restoreDirectory = null,
                            string? hidHideStatePath = null)
    {
        ProfileDirectory = profileDirectory ?? ProfileStore.CanonicalDirectory;
        RestoreDirectory = restoreDirectory ?? DefaultRestoreDirectory;
        _hidHideStatePath = hidHideStatePath;
    }

    // ---------------------------------------------------------------- survey

    /// <summary>What a cleanup would find. Read-only, needs no elevation, changes nothing.</summary>
    public sealed class Plan
    {
        public string? AutoStartCommand { get; set; }
        public string? LegacyAutoStartCommand { get; set; }
        public List<string> HidHideEntries { get; set; } = [];
        public bool HidHideInstalled { get; set; }
        public bool NeedsElevationForHidHide { get; set; }

        /// <summary>Devices Dovetail hid and will un-hide, from the recorded state.</summary>
        public List<string> HidHideDevicesToUnhide { get; set; } = [];

        /// <summary>True when Dovetail turned cloaking on and will turn it back off.</summary>
        public bool WillTurnCloakOff { get; set; }
        public string ProfileDirectory { get; set; } = "";
        public int ProfileFileCount { get; set; }
        public bool ProfilesExist => ProfileFileCount > 0;

        public bool AnythingToDo =>
            AutoStartCommand is not null || LegacyAutoStartCommand is not null
            || HidHideEntries.Count > 0 || ProfilesExist
            || HidHideDevicesToUnhide.Count > 0 || WillTurnCloakOff;
    }

    public Plan Survey()
    {
        var plan = new Plan
        {
            AutoStartCommand = AutoStart.CurrentCommand(),
            LegacyAutoStartCommand = AutoStart.LegacyCommand(),
            HidHideInstalled = HidHideAccess.Installed,
            ProfileDirectory = ProfileDirectory,
        };

        if (plan.HidHideInstalled)
        {
            plan.HidHideEntries = OurAllowListEntries();

            var state = HidHideState.Load(_hidHideStatePath);
            var hidden = HidHideAccess.HiddenDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan.HidHideDevicesToUnhide = state.DevicesWeHid.Where(hidden.Contains).ToList();
            plan.WillTurnCloakOff = state.WeTurnedCloakOn && HidHideAccess.Cloaking();

            plan.NeedsElevationForHidHide =
                (plan.HidHideEntries.Count > 0 || plan.HidHideDevicesToUnhide.Count > 0 || plan.WillTurnCloakOff)
                && !HidHideAccess.IsElevated();
        }

        try
        {
            if (plan.ProfileDirectory.Length > 0 && Directory.Exists(plan.ProfileDirectory))
                plan.ProfileFileCount = Directory
                    .GetFiles(plan.ProfileDirectory, "*", SearchOption.AllDirectories).Length;
        }
        catch { /* an unreadable folder is reported as empty rather than failing the survey */ }

        return plan;
    }

    /// <summary>
    /// Allow-list paths that are ours, matched on file name. Path comparison is not usable
    /// here: an installation that has been moved or reinstalled elsewhere leaves entries whose
    /// path no longer resolves, and those stale ones are exactly what an uninstall should take
    /// with it.
    /// </summary>
    public static List<string> OurAllowListEntries() => FilterOurs(HidHideAccess.AllowedApps());

    /// <summary>
    /// The "only ours" rule, separated from the CLI that supplies the list so it can be
    /// checked directly against a list containing other people's entries. This is the one
    /// decision in the uninstaller that can break unrelated software if it is wrong, so it is
    /// under test rather than merely commented.
    /// </summary>
    public static List<string> FilterOurs(IEnumerable<string> allowList)
    {
        var ours = HidHideAccess.AllOwnNames();
        return allowList
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0 && ours.Contains(Path.GetFileName(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- run

    /// <summary>What a cleanup actually did, and where the restore point went.</summary>
    public sealed class RestorePoint
    {
        public string CreatedUtc { get; set; } = "";
        public string ProductVersion { get; set; } = "";
        public string? AutoStartValueName { get; set; }
        public string? AutoStartCommand { get; set; }
        public string? LegacyAutoStartValueName { get; set; }
        public string? LegacyAutoStartCommand { get; set; }
        public List<string> HidHideEntriesRemoved { get; set; } = [];
        public List<string> HidHideDevicesUnhidden { get; set; } = [];
        public bool CloakTurnedOff { get; set; }
        public string? ProfilesMovedFrom { get; set; }
        public string? ProfilesMovedTo { get; set; }
    }

    public sealed class Result
    {
        public bool Success { get; set; } = true;
        public string? RestorePointPath { get; set; }
        public List<string> Log { get; set; } = [];
        public List<string> Problems { get; set; } = [];
        public RestorePoint Removed { get; set; } = new();
    }

    /// <summary>
    /// Removes what Dovetail put on this machine.
    ///
    /// <paramref name="removeProfiles"/> is a decision, never a default. The caller asks the
    /// user and passes the answer in; nothing here guesses, because the wrong guess destroys
    /// measurements that cost a full input sweep per pad to recreate. Even when true, the
    /// profiles are moved into the restore folder rather than deleted.
    /// </summary>
    public Result Run(bool removeProfiles, Action<string>? log = null)
    {
        var r = new Result();
        void L(string m) { r.Log.Add(m); log?.Invoke(m); }
        void Problem(string m) { r.Problems.Add(m); r.Success = false; L("PROBLEM: " + m); }

        r.Removed.CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'");
        r.Removed.ProductVersion = typeof(UninstallCleanup).Assembly.GetName().Version?.ToString() ?? "";

        // ---- 1. auto-start ----
        string? current = AutoStart.CurrentCommand();
        string? legacy = AutoStart.LegacyCommand();

        if (current is null && legacy is null)
        {
            L("auto-start: no entry to remove");
        }
        else
        {
            if (current is not null)
            {
                r.Removed.AutoStartValueName = AutoStartValueNameForRestore;
                r.Removed.AutoStartCommand = current;
            }
            if (legacy is not null)
            {
                r.Removed.LegacyAutoStartValueName = AutoStart.LegacyValueName;
                r.Removed.LegacyAutoStartCommand = legacy;
            }

            // Disable deletes both value names and leaves every other Run entry untouched.
            if (AutoStart.Disable())
                L($"auto-start: removed {(current is not null ? "the entry" : "")}" +
                  $"{(current is not null && legacy is not null ? " and " : "")}" +
                  $"{(legacy is not null ? "the pre-rename entry" : "")}, other startup items untouched");
            else
                Problem("auto-start: the Run value could not be deleted");
        }

        // ---- 2. HidHide allow list ----
        if (!HidHideAccess.Installed)
        {
            L("HidHide: not installed, no allow list to edit");
        }
        else
        {
            var ours = OurAllowListEntries();
            if (ours.Count == 0)
            {
                L("HidHide: nothing of ours on the allow list");
            }
            else if (!HidHideAccess.IsElevated())
            {
                Problem($"HidHide: {ours.Count} entry/entries of ours are still on the allow list, " +
                        "and editing it needs administrator rights. Re-run this elevated.");
            }
            else
            {
                foreach (var entry in ours)
                {
                    if (HidHideAccess.Unregister(entry))
                    {
                        r.Removed.HidHideEntriesRemoved.Add(entry);
                        L($"HidHide: removed {entry}");
                    }
                    else Problem($"HidHide: could not remove {entry}");
                }

                var left = OurAllowListEntries();
                if (left.Count > 0) Problem("HidHide: still listed after removal: " + string.Join(", ", left));

                int othersKept = HidHideAccess.AllowedApps().Count;
                L($"HidHide: {othersKept} entry/entries belonging to other software kept");
            }

            UndoOurHidHideConfig(r, L, Problem);
        }

        // ---- 3. calibration profiles, only when asked ----
        string profiles = ProfileDirectory;
        if (!removeProfiles)
        {
            L(profiles.Length > 0 && Directory.Exists(profiles)
                ? $"profiles: kept at {profiles}, as chosen"
                : "profiles: nothing stored, nothing to keep or remove");
        }
        else if (profiles.Length == 0 || !Directory.Exists(profiles))
        {
            L("profiles: nothing stored, nothing to remove");
        }
        else
        {
            string dest = Path.Combine(RestoreDirectory, $"profiles-{DateTime.Now:yyyyMMdd-HHmmss}");
            try
            {
                Directory.CreateDirectory(RestoreDirectory);
                Directory.Move(profiles, dest);
                r.Removed.ProfilesMovedFrom = profiles;
                r.Removed.ProfilesMovedTo = dest;
                L($"profiles: moved out of {profiles} to {dest}");
            }
            catch (Exception ex)
            {
                Problem($"profiles: could not move {profiles} aside, {ex.Message}");
            }
        }

        L("ViGEmBus and HidHide were left installed. Remove them from " +
          "Settings > Apps > Installed apps if nothing else on this machine uses them.");

        // ---- restore point ----
        try
        {
            Directory.CreateDirectory(RestoreDirectory);
            string path = Path.Combine(RestoreDirectory, $"restore-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(r.Removed, Json));
            r.RestorePointPath = path;
            L($"restore point written to {path}");
        }
        catch (Exception ex)
        {
            Problem($"the restore point could not be written, this cleanup is not reversible: {ex.Message}");
        }

        return r;
    }

    /// <summary>
    /// Reverses the two HidHide settings setup made, and only those.
    ///
    /// The recorded state is the authority on ownership. A device the user or DS4Windows hid is
    /// not in it and is not touched. Cloaking is turned back off only when Dovetail turned it on
    /// AND nothing is left hidden that would need it: another application's hidden device is a
    /// reason to leave the cloak alone, because turning it off would un-hide that device too.
    /// </summary>
    private void UndoOurHidHideConfig(Result r, Action<string> L, Action<string> Problem)
    {
        var state = HidHideState.Load(_hidHideStatePath);
        if (!state.AnythingRecorded)
        {
            L("HidHide: Dovetail made no cloak or hidden-device changes to undo");
            return;
        }

        if (!HidHideAccess.IsElevated())
        {
            Problem("HidHide: undoing the device hiding Dovetail set up needs administrator rights. " +
                    "Re-run this elevated, or the controller stays hidden from other software.");
            return;
        }

        var hidden = HidHideAccess.HiddenDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in state.DevicesWeHid)
        {
            if (!hidden.Contains(id)) { L($"HidHide: {id} is already not hidden"); continue; }
            if (HidHideAccess.UnhideDevice(id))
            {
                r.Removed.HidHideDevicesUnhidden.Add(id);
                L($"HidHide: un-hid {id}, which Dovetail had hidden");
            }
            else Problem($"HidHide: could not un-hide {id}");
        }

        if (state.WeTurnedCloakOn && HidHideAccess.Cloaking())
        {
            int othersStillHidden = HidHideAccess.HiddenDevices().Count;
            if (othersStillHidden > 0)
            {
                L($"HidHide: cloaking left on, because {othersStillHidden} device(s) hidden by " +
                  "other software still need it");
            }
            else if (HidHideAccess.SetCloak(false))
            {
                r.Removed.CloakTurnedOff = true;
                L("HidHide: cloaking turned back off, as Dovetail found it");
            }
            else Problem("HidHide: could not turn cloaking back off");
        }

        HidHideState.Delete(_hidHideStatePath);
    }

    // ---------------------------------------------------------------- restore

    /// <summary>
    /// Puts back what a cleanup removed. The mirror of <see cref="Run"/>: the Run value is
    /// rewritten with the command it held, our allow-list entries are re-registered, and moved
    /// profiles are moved back. Anything that is already there again is left alone rather than
    /// overwritten, so restoring twice cannot clobber a fresh install's data.
    /// </summary>
    public Result Restore(string restorePointPath, Action<string>? log = null)
    {
        var r = new Result();
        void L(string m) { r.Log.Add(m); log?.Invoke(m); }
        void Problem(string m) { r.Problems.Add(m); r.Success = false; L("PROBLEM: " + m); }

        RestorePoint rp;
        try
        {
            rp = JsonSerializer.Deserialize<RestorePoint>(File.ReadAllText(restorePointPath), Json)
                 ?? throw new InvalidDataException("the restore point is empty");
        }
        catch (Exception ex)
        {
            Problem($"could not read {restorePointPath}: {ex.Message}");
            return r;
        }

        r.Removed = rp;
        L($"restoring the cleanup of {rp.CreatedUtc}");

        // ---- auto-start ----
        if (rp.AutoStartCommand is { Length: > 0 } cmd)
        {
            if (AutoStart.IsEnabled())
            {
                L("auto-start: an entry is already present, left as it is");
            }
            else
            {
                try
                {
                    Microsoft.Win32.Registry.SetValue(
                        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
                        rp.AutoStartValueName ?? AutoStartValueNameForRestore, cmd);
                    L($"auto-start: restored -> {cmd}");
                }
                catch (Exception ex) { Problem($"auto-start: could not restore, {ex.Message}"); }
            }
        }
        else L("auto-start: nothing was removed, nothing to restore");

        // The pre-rename value is deliberately NOT restored. It pointed at Bridge.exe, which
        // no build produces any more, so putting it back would recreate the stale entry the
        // rename existed to clear.
        if (rp.LegacyAutoStartCommand is { Length: > 0 })
            L("auto-start: the pre-rename entry is not restored; it named an executable that " +
              "no longer exists. Its command is recorded in the restore point.");

        // ---- HidHide ----
        bool anyHidHide = rp.HidHideEntriesRemoved.Count > 0
                          || rp.HidHideDevicesUnhidden.Count > 0 || rp.CloakTurnedOff;
        if (!anyHidHide)
        {
            L("HidHide: nothing was removed, nothing to restore");
        }
        else if (!HidHideAccess.Installed)
        {
            Problem("HidHide: no longer installed, its configuration cannot be restored");
        }
        else if (!HidHideAccess.IsElevated())
        {
            Problem("HidHide: restoring its configuration needs administrator rights");
        }
        else
        {
            var already = HidHideAccess.AllowedApps()
                .Select(p => p.Trim().Trim('"'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The allow list first, then the hiding, then the cloak: the same order setup uses,
            // and for the same reason. Restoring the cloak before we are back on the list would
            // briefly hide the pad from Dovetail.
            foreach (var entry in rp.HidHideEntriesRemoved)
            {
                if (already.Contains(entry)) { L($"HidHide: {entry} is already listed"); continue; }
                if (HidHideAccess.RegisterPath(entry)) L($"HidHide: restored {entry}");
                else Problem($"HidHide: could not restore {entry}");
            }

            var state = HidHideState.Load(_hidHideStatePath);
            var hidden = HidHideAccess.HiddenDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in rp.HidHideDevicesUnhidden)
            {
                if (hidden.Contains(id)) { L($"HidHide: {id} is already hidden"); continue; }
                if (HidHideAccess.HideDevice(id))
                {
                    L($"HidHide: re-hid {id}");
                    if (!state.DevicesWeHid.Contains(id, StringComparer.OrdinalIgnoreCase))
                        state.DevicesWeHid.Add(id);
                }
                else Problem($"HidHide: could not re-hide {id}");
            }

            if (rp.CloakTurnedOff && !HidHideAccess.Cloaking())
            {
                if (HidHideAccess.SetCloak(true)) { L("HidHide: cloaking turned back on"); state.WeTurnedCloakOn = true; }
                else Problem("HidHide: could not turn cloaking back on");
            }

            // Put the ownership record back too, or a later uninstall would think these
            // settings were the user's and leave the pad hidden with nothing allowed through.
            if (state.AnythingRecorded) state.Save(_hidHideStatePath);
        }

        // ---- profiles ----
        if (rp.ProfilesMovedTo is { Length: > 0 } from && rp.ProfilesMovedFrom is { Length: > 0 } to)
        {
            if (!Directory.Exists(from)) Problem($"profiles: the moved copy is no longer at {from}");
            else if (Directory.Exists(to)) Problem($"profiles: {to} exists again; the moved copy is still at {from}, move it back by hand if that is what you want");
            else
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    Directory.Move(from, to);
                    L($"profiles: restored to {to}");
                }
                catch (Exception ex) { Problem($"profiles: could not restore, {ex.Message}"); }
            }
        }
        else L("profiles: none were removed, nothing to restore");

        return r;
    }

    /// <summary>Most recent restore point, or null when there is none.</summary>
    public static string? LatestRestorePoint(string? restoreDirectory = null)
    {
        try
        {
            string dir = restoreDirectory ?? DefaultRestoreDirectory;
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "restore-*.json")
                            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
        }
        catch { return null; }
    }

    // AutoStart keeps its value name private, which is right for everything except a restore
    // point that has to name it years later. Mirrored here rather than made public there, so
    // the only writer of that value stays AutoStart itself.
    private const string AutoStartValueNameForRestore = "Dovetail Gamepad Emulator";
}
