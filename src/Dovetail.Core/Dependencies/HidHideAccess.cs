using System.Diagnostics;
using System.Security.Principal;

namespace Dovetail.Core;

/// <summary>
/// Keeps this application on HidHide's allow list.
///
/// HidHide's whole job is to make the physical pad invisible to everything that is not on its
/// list, which is what stops a game seeing the raw controller and the virtual one at the same
/// time. That protection applies to us as well, and it is enforced by executable path. An
/// application that installs HidHide, cloaks the pad and then forgets to register itself has
/// hidden the controller from its own translation engine, and every attempt to open the device
/// comes back as Win32 error 5. The symptom looks exactly like an unplugged controller.
///
/// This was a live fault, not a hypothetical one. The allow list on the development machine
/// named the build-output paths of two console tools and did not name the application at all,
/// so the packaged build in dist could not read the pad even though the same code read it fine
/// when run from the build tree. That is the same "works here, dead once installed" class of
/// defect as the tool lookup fixed in Stage 5, one layer down.
///
/// Registration needs administrator rights, so the work happens in the elevated helper that
/// already installs the drivers. Everything here that only reads is safe to call unelevated.
/// </summary>
public static class HidHideAccess
{
    /// <summary>The executables that make up this application and all need device access.</summary>
    public static readonly string[] OwnExecutables =
        ["Dovetail.exe", "dovetail-diag.exe", "dovetail-engine.exe"];

    /// <summary>Every file name that is ours, for matching allow-list entries.</summary>
    public static HashSet<string> AllOwnNames() =>
        OwnExecutables.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] CandidatePaths =
    [
        @"C:\Program Files\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
        @"C:\Program Files\Nefarius Software Solutions\HidHide\HidHideCLI.exe",
        @"C:\Program Files (x86)\Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe",
    ];

    /// <summary>Full path to HidHideCLI.exe, or null when HidHide is not installed.</summary>
    public static string? Cli()
    {
        foreach (var p in CandidatePaths)
            if (File.Exists(p)) return p;
        return null;
    }

    public static bool Installed => Cli() is not null;

    /// <summary>
    /// True when HidHide is actively hiding devices. With cloaking off the allow list does not
    /// matter, so there is nothing to warn about and nothing to fix.
    /// </summary>
    public static bool Cloaking()
    {
        var outp = RunCli("--cloak-state");
        return outp is not null && outp.Contains("--cloak-on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Device instance ids HidHide is currently hiding.</summary>
    public static List<string> HiddenDevices() => Parse(RunCli("--dev-list"), "--dev-hide");

    /// <summary>Executable paths currently allowed through the cloak.</summary>
    public static List<string> AllowedApps() => Parse(RunCli("--app-list"), "--app-reg");

    /// <summary>
    /// The executables of this installation that sit next to the running one and are not yet on
    /// the allow list. Paths are compared case-insensitively, as Windows does.
    /// </summary>
    public static List<string> MissingRegistrations()
    {
        var allowed = AllowedApps()
            .Select(p => p.Trim().Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var exe in OwnExecutables)
        {
            string full = Path.Combine(AppContext.BaseDirectory, exe);
            if (File.Exists(full) && !allowed.Contains(full)) missing.Add(full);
        }
        return missing;
    }

    /// <summary>
    /// Whether the pad is hidden from us right now: HidHide installed, cloaking, at least one
    /// device hidden, and something of ours not on the list. This is the condition that turns a
    /// working controller into "no controller found", so it is worth naming precisely rather
    /// than letting the caller guess from an error code.
    /// </summary>
    public static bool BlockingUs() =>
        Installed && Cloaking() && HiddenDevices().Count > 0 && MissingRegistrations().Count > 0;

    /// <summary>
    /// Adds this installation's executables to the allow list, and drops stale entries that
    /// carry one of our file names but point at a path that no longer exists.
    ///
    /// Only our own file names are ever removed. The list belongs to the operator and may well
    /// contain DS4Windows or Steam; clearing entries that are not ours would break those.
    ///
    /// Requires elevation, which is why <see cref="IsElevated"/> is checked first rather than
    /// leaving the caller to interpret a silent no-op from the CLI.
    /// </summary>
    public static bool Register(Action<string>? log = null)
    {
        log ??= _ => { };

        if (Cli() is null) { log("HidHide is not installed, so there is no allow list to edit."); return false; }
        if (!IsElevated()) { log("Registering with HidHide needs administrator rights."); return false; }

        var ourNames = AllOwnNames();
        int changed = 0;

        foreach (var stale in AllowedApps()
                     .Select(p => p.Trim().Trim('"'))
                     .Where(p => ourNames.Contains(Path.GetFileName(p)) && !File.Exists(p))
                     .ToList())
        {
            log($"removing stale entry {stale}");
            RunCli("--app-unreg", stale);
            changed++;
        }

        foreach (var full in MissingRegistrations())
        {
            log($"allowing {full}");
            RunCli("--app-reg", full);
            changed++;
        }

        if (changed == 0) { log("already on the allow list, nothing to change."); return true; }

        var left = MissingRegistrations();
        if (left.Count == 0) { log($"allow list updated, {changed} change(s)."); return true; }

        log("allow list still missing: " + string.Join(", ", left));
        return false;
    }

    /// <summary>
    /// Takes one path off the allow list and confirms it is gone.
    ///
    /// The CLI reports nothing useful on failure, so the check is done by reading the list
    /// back rather than by trusting an exit code. Used by the uninstaller, which must be able
    /// to say truthfully whether the machine was left clean. The caller decides which paths
    /// are ours; this will remove any path it is given, so it is never handed the whole list.
    /// </summary>
    public static bool Unregister(string fullPath)
    {
        if (Cli() is null || !IsElevated()) return false;
        RunCli("--app-unreg", fullPath);
        return !AllowedApps()
            .Select(p => p.Trim().Trim('"'))
            .Contains(fullPath, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Puts one path back on the allow list, for the uninstaller's restore path. Confirmed by
    /// reading the list back, the same way <see cref="Unregister"/> is.
    /// </summary>
    public static bool RegisterPath(string fullPath)
    {
        if (Cli() is null || !IsElevated()) return false;
        RunCli("--app-reg", fullPath);
        return AllowedApps()
            .Select(p => p.Trim().Trim('"'))
            .Contains(fullPath, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ configuring

    // **Device instance ids are compared case-insensitively here and passed to the CLI exactly
    // as HidHide itself spells them, and that is not fussiness.** Measured on this machine:
    // HidHide stores whatever string it is given, verbatim and without normalising, and matches
    // it case-sensitively. Windows' own enumeration returns
    //   HID\VID_0810&PID_0001&COL02\7&744114A&0&0001
    // while an entry added through HidHide's own client reads
    //   HID\VID_0810&PID_0001&Col01\7&744114a&0&0000
    // for the sibling collection of the same device. So --dev-unhide with the enumerated
    // spelling silently does nothing to an entry the user's client wrote, and --dev-hide with
    // it would add a second entry for a device already hidden. Both are silent: the CLI reports
    // success either way. Every call therefore looks the id up in the live list first and hands
    // the CLI HidHide's own spelling, and every result is confirmed by reading the list back.

    /// <summary>
    /// Whether two device instance ids name the same device. Case-insensitive, because Windows
    /// device paths are, and because the two sources this code reads disagree about case for
    /// the same physical device. Public so the rule itself is under test.
    /// </summary>
    public static bool SameDeviceId(string a, string b) =>
        a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>HidHide's own spelling of an id it currently hides, or null if it hides no such device.</summary>
    private static string? AsHidHideSpellsIt(string instanceId) =>
        HiddenDevices().FirstOrDefault(d => SameDeviceId(d, instanceId));

    /// <summary>
    /// Hides one device from everything not on the allow list. A device already hidden under
    /// any spelling is left exactly as it is rather than added a second time.
    /// </summary>
    public static bool HideDevice(string instanceId)
    {
        if (Cli() is null || !IsElevated()) return false;
        if (AsHidHideSpellsIt(instanceId) is not null) return true;
        RunCli("--dev-hide", instanceId);
        return HiddenDevices().Contains(instanceId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stops hiding one device. Used only to undo a hide Dovetail itself added.</summary>
    public static bool UnhideDevice(string instanceId)
    {
        if (Cli() is null || !IsElevated()) return false;
        // HidHide's spelling, not ours, or a case difference makes this a silent no-op.
        if (AsHidHideSpellsIt(instanceId) is not { } actual) return true;   // already not hidden
        RunCli("--dev-unhide", actual);
        return !HiddenDevices().Contains(instanceId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Turns the global cloak on or off. Confirmed by reading the state back.</summary>
    public static bool SetCloak(bool on)
    {
        if (Cli() is null || !IsElevated()) return false;
        RunCli(on ? "--cloak-on" : "--cloak-off");
        return Cloaking() == on;
    }

    /// <summary>What <see cref="Configure"/> changed, so an uninstall can undo exactly that.</summary>
    public sealed class Applied
    {
        public List<string> DevicesHidden { get; set; } = [];
        public bool CloakTurnedOn { get; set; }
        public List<string> Log { get; set; } = [];
        public bool Success { get; set; } = true;
    }

    /// <summary>
    /// Makes HidHide actually do its job for this installation: allow our executables through,
    /// hide the physical pad, and turn cloaking on.
    ///
    /// **The order is the whole point and it is not arbitrary.** It is the order Findings Log
    /// 4.2 used, for the reason recorded there: register ourselves BEFORE hiding anything. Hide
    /// first and there is a window in which the pad is cloaked and Dovetail is not on the list,
    /// so the running engine loses the controller mid-change and the operator sees the pad
    /// disappear during setup.
    ///
    /// **Only additions are reported.** A device already hidden, or a cloak already on, is left
    /// alone and NOT recorded in <see cref="Applied"/>, because the uninstaller undoes what this
    /// added and must not undo a setting that was the user's or another application's. On a
    /// machine already configured this whole call is a no-op that says so.
    /// </summary>
    public static Applied Configure(IEnumerable<string> deviceInstanceIds, Action<string>? log = null)
    {
        var applied = new Applied();
        void L(string m) { applied.Log.Add(m); log?.Invoke(m); }

        if (Cli() is null)
        {
            applied.Success = false;
            L("HidHide is not installed, so there is nothing to configure.");
            return applied;
        }
        if (!IsElevated())
        {
            applied.Success = false;
            L("Configuring HidHide needs administrator rights.");
            return applied;
        }

        // 1. us first, always
        if (!Register(L)) applied.Success = false;

        // 2. then the pad
        var alreadyHidden = HiddenDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in deviceInstanceIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (alreadyHidden.Contains(id)) { L($"already hidden, left alone: {id}"); continue; }
            if (HideDevice(id)) { applied.DevicesHidden.Add(id); L($"hiding {id}"); }
            else { applied.Success = false; L($"could not hide {id}"); }
        }

        // 3. then the cloak
        if (Cloaking()) L("cloaking was already on, left alone");
        else if (SetCloak(true)) { applied.CloakTurnedOn = true; L("cloaking turned on"); }
        else { applied.Success = false; L("could not turn cloaking on"); }

        if (applied.DevicesHidden.Count == 0 && !applied.CloakTurnedOn)
            L("HidHide was already configured; nothing was changed.");

        return applied;
    }

    /// <summary>
    /// True when HidHide is installed but is not actually hiding this pad, so a game can still
    /// bind to the raw device. Distinct from <see cref="BlockingUs"/>, which is the opposite
    /// fault: cloaking on and Dovetail not allowed through.
    /// </summary>
    public static bool NeedsConfiguring(IEnumerable<string> deviceInstanceIds)
    {
        if (!Installed) return false;
        if (!Cloaking()) return true;
        var hidden = HiddenDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return deviceInstanceIds.Any(id => !hidden.Contains(id)) || MissingRegistrations().Count > 0;
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ plumbing

    private static string? RunCli(params string[] args)
    {
        string? cli = Cli();
        if (cli is null) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // The CLI resolves relative paths against its own directory, so run it there.
                WorkingDirectory = Path.GetDirectoryName(cli)!,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return outp;
        }
        catch { return null; }
    }

    private static List<string> Parse(string? output, string prefix)
    {
        var result = new List<string>();
        if (output is null) return result;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[prefix.Length..].Trim();
            result.Add(rest.Trim('"'));
        }
        return result;
    }
}
