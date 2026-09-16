using System.Text.Json;
using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Checks the Section 7 state machine: slot identity, "no profile means no virtual device",
/// naming, forgetting, per-game overrides, first-run gating and auto-start.
///
/// This runs against real files in a throwaway directory rather than against mocks, because
/// every one of these behaviours is defined by what ends up on disk, and the defects worth
/// catching here are persistence defects: a rename that does not survive a reload, a forget
/// that leaves the slot claimed, a game profile that quietly rewrites a calibration file.
/// Nothing here needs a controller.
/// </summary>
internal static class Stage5Test
{
    private static int _pass, _fail;
    private static readonly List<string> _failures = [];

    private static void Check(string what, object? actual, object? expected)
    {
        string a = actual?.ToString() ?? "(null)", x = expected?.ToString() ?? "(null)";
        bool ok = a == x;
        if (ok) _pass++; else { _fail++; _failures.Add($"{what}: got {a}, expected {x}"); }
        Console.WriteLine($"   {(ok ? "PASS" : "FAIL")}  {what,-58} {a}");
    }

    private static void Ok(string what, bool condition)
    {
        if (condition) _pass++; else { _fail++; _failures.Add(what); }
        Console.WriteLine($"   {(condition ? "PASS" : "FAIL")}  {what}");
    }

    internal static int Run(string[] args)
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - STAGE 5 STATE MACHINE TEST (no controller needed)");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        // Seed the sandbox from the real calibrations, so the test runs against the same
        // shaped data the app sees rather than against invented numbers.
        string real = Program.ArgValue(args, "--dir") ?? Program.DefaultProfileDirectory();
        string dir = Path.Combine(Path.GetTempPath(), "dovetail-stage5-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Console.WriteLine($" scratch directory : {dir}");
        Console.WriteLine($" seeded from       : {real}");
        Console.WriteLine();

        try
        {
            foreach (var f in Directory.GetFiles(real, "slot*.calibration.json"))
                File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));

            SlotIdentity(dir);
            NoProfileNoDevice(dir);
            NamingRules(dir);
            ForgetAndReclaim(dir);
            GameOverrides(dir);
            FirstRunGating(dir);
            PortFileMigration();
            AutoStartRoundTrip();
            ProfileStorage();
            UninstallCleanupRoundTrip();
            StickFeelEditing(dir);
            DependencyContract();
            HidHideOwnership(dir);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 74));
        Console.WriteLine($" {_pass} passed, {_fail} failed");
        foreach (var f in _failures) Console.WriteLine("   FAILED: " + f);
        Console.WriteLine(new string('-', 74));
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 1. slot identity

    private static void SlotIdentity(string dir)
    {
        Console.WriteLine(" 1. SLOT IDENTITY - a profile belongs to a player number");
        var reg = SlotRegistry.Load(dir);
        Check("calibrated slots found", reg.CalibratedCount, 2);
        Check("slot 1 file name", SlotRegistry.FileNameForSlot(1), "slot1.calibration.json");
        Check("slot 1 SlotNumber matches its file", reg.ForSlot(1)?.SlotNumber, 1);
        Check("slot 2 SlotNumber matches its file", reg.ForSlot(2)?.SlotNumber, 2);

        // Claim order is the whole identity model: first pad to connect becomes player 1.
        var taken = new List<int>();
        int? first = reg.ClaimNextSlot(taken); if (first is int f1) taken.Add(f1);
        int? second = reg.ClaimNextSlot(taken); if (second is int f2) taken.Add(f2);
        Check("first pad to connect claims", first, 1);
        Check("second pad to connect claims", second, 2);
        Console.WriteLine();
    }

    // ------------------------------------------------- 2. no profile means no virtual device

    private static void NoProfileNoDevice(string dir)
    {
        Console.WriteLine(" 2. NO PROFILE = NO VIRTUAL DEVICE - the approved rule for unknown pads");
        var reg = SlotRegistry.Load(dir);
        var taken = new List<int> { 1, 2 };

        int? third = reg.ClaimNextSlot(taken);
        Check("third pad, only two calibrated, claims", third, null);
        Ok("null claim is what the engine turns into 'create no device'", third is null);

        // And with nothing calibrated at all, the very first pad is still refused rather
        // than silently given another unit's numbers.
        string empty = Path.Combine(dir, "empty");
        Directory.CreateDirectory(empty);
        var bare = SlotRegistry.Load(empty);
        Check("nothing calibrated: slots", bare.CalibratedCount, 0);
        Check("nothing calibrated: first pad claims", bare.ClaimNextSlot([]), null);
        Check("nothing calibrated: next free slot to offer", bare.NextFreeSlotNumber(), 1);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 3. naming

    private static void NamingRules(string dir)
    {
        Console.WriteLine(" 3. NAMING - custom name optional, defaults are player names not device strings");
        var reg = SlotRegistry.Load(dir);
        string before = reg.ForSlot(1)!.EffectiveName;
        Console.WriteLine($"      seeded name: \"{before}\"");

        reg.Rename(1, "Test Custom Name");
        var reloaded = SlotRegistry.Load(dir);
        Check("rename survives a reload", reloaded.ForSlot(1)?.EffectiveName, "Test Custom Name");
        Check("rename did not move the slot", reloaded.ForSlot(1)?.SlotNumber, 1);

        // Clearing the name falls back to a player-style default, never to the HID product
        // string, which on this receiver is a generic name shared by both pads.
        reloaded.Rename(1, "");
        var cleared = SlotRegistry.Load(dir);
        Check("cleared name falls back to", cleared.ForSlot(1)?.EffectiveName, "Dovetail Controller 1");
        Ok("fallback is not the HID product string",
            cleared.ForSlot(1)!.EffectiveName != cleared.ForSlot(1)!.Device.Product);

        cleared.Rename(1, before);
        Check("restored for later checks", SlotRegistry.Load(dir).ForSlot(1)?.EffectiveName, before);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 4. forget

    private static void ForgetAndReclaim(string dir)
    {
        Console.WriteLine(" 4. FORGET - deletes calibration, frees the slot, next pad is brand new");
        var reg = SlotRegistry.Load(dir);
        string path2 = SlotRegistry.PathForSlot(dir, 2);
        string backup = path2 + ".bak";
        File.Copy(path2, backup, true);

        Ok("slot 2 file exists before", File.Exists(path2));
        Check("forget reports it had something", reg.Forget(2), true);
        Ok("slot 2 file is gone after", !File.Exists(path2));

        var after = SlotRegistry.Load(dir);
        Check("calibrated slots after forget", after.CalibratedCount, 1);
        Check("slot 2 no longer resolves", after.ForSlot(2), null);
        Check("a pad connecting alongside player 1 now claims", after.ClaimNextSlot([1]), null);
        Ok("so a forgotten controller gets no virtual device until recalibrated",
            after.ClaimNextSlot([1]) is null);
        Check("forget on an empty slot reports nothing to do", after.Forget(2), false);
        Check("the freed slot is what calibration offers next", after.NextFreeSlotNumber(), 2);

        File.Copy(backup, path2, true);
        File.Delete(backup);
        Check("restored slot 2", SlotRegistry.Load(dir).CalibratedCount, 2);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 5. game overrides

    private static void GameOverrides(string dir)
    {
        Console.WriteLine(" 5. PER-GAME PROFILES - a lens over a calibration, never an edit of it");
        var reg = SlotRegistry.Load(dir);
        var prof = reg.ForSlot(1)!;
        var ax = prof.Axes["leftStickX"];
        int baseDz = ax.Deadzone, baseSat = ax.Saturation, baseAnti = ax.AntiDeadzone;
        int baseCentre = ax.Centre;
        Console.WriteLine($"      calibration: deadzone {baseDz}, anti-deadzone {baseAnti}, saturation {baseSat}");

        string path1 = SlotRegistry.PathForSlot(dir, 1);
        byte[] before = File.ReadAllBytes(path1);

        var game = new GameProfile
        {
            Name = "Test Game",
            ExecutableName = "testgame.exe",
            Deadzone = baseDz + 3,
            Saturation = baseSat - 10,
            ResponseCurve = 1.4,
        };
        game.ApplyTo(prof);

        Check("deadzone overridden", prof.Axes["leftStickX"].Deadzone, baseDz + 3);
        Check("saturation overridden", prof.Axes["leftStickX"].Saturation, baseSat - 10);
        Check("curve overridden", prof.Axes["leftStickX"].ResponseCurve, 1.4);
        Check("anti-deadzone left alone when the game does not set it",
              prof.Axes["leftStickX"].AntiDeadzone, baseAnti);
        Check("measured centre is NOT touched by a game profile",
              prof.Axes["leftStickX"].Centre, baseCentre);
        Ok("the calibration file on disk is byte-identical after applying",
           File.ReadAllBytes(path1).SequenceEqual(before));

        // Reloading is how the app drops a game's overrides when the game exits.
        var fresh = SlotRegistry.Load(dir);
        Check("reload restores the calibrated deadzone", fresh.ForSlot(1)?.Axes["leftStickX"].Deadzone, baseDz);
        Check("reload restores the calibrated saturation", fresh.ForSlot(1)?.Axes["leftStickX"].Saturation, baseSat);

        // Registration and lookup.
        var lib = new GameLibrary();
        var added = lib.Add(@"C:\Games\Sekiro\sekiro.exe", "Sekiro");
        Check("registered by executable name", added.ExecutableName, "sekiro.exe");
        Check("library count", lib.Games.Count, 1);
        string gpath = GameLibrary.DefaultPath(dir);
        lib.Save(gpath);
        Ok("games.json written", File.Exists(gpath));
        Check("reloaded library keeps the entry", GameLibrary.Load(gpath).Games.Count, 1);
        Check("a game that is not running is not matched", GameLibrary.Load(gpath).FindRunning(), null);

        // A game entry pointing at this very process proves the matcher without launching
        // anything, which is the only honest way to test it here.
        var selfLib = new GameLibrary();
        selfLib.Add(Environment.ProcessPath ?? "dovetail-engine.exe", "This Test Process");
        Check("a running process IS matched", selfLib.FindRunning()?.Name, "This Test Process");
        foreach (var g in selfLib.Games) g.Enabled = false;
        Check("a disabled entry is ignored even while running", selfLib.FindRunning(), null);
        Check("remove works", lib.Remove("sekiro.exe"), true);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 6. first run

    private static void FirstRunGating(string dir)
    {
        Console.WriteLine(" 6. FIRST RUN - window once, then tray only, handover shown exactly once");
        string sp = Path.Combine(dir, "settings-test.json");
        var s = DovetailSettings.Load(sp);
        Check("fresh state: first run not completed", s.FirstRunCompleted, false);
        Check("fresh state: handover not yet explained", s.HandoverExplained, false);
        Check("fresh state: auto-start undecided", s.AutoStartOnLogin, null);
        Check("fresh state: auto-start not yet asked", s.AutoStartPromptShown, false);
        Check("fresh state: connect notices on by default", s.ShowConnectNotifications, true);
        Check("fresh state: unknown-pad notices on by default", s.ShowUnrecognisedNotifications, true);

        s.FirstRunCompleted = true;
        s.HandoverExplained = true;
        s.AutoStartPromptShown = true;
        s.AutoStartOnLogin = false;
        s.ShowDisconnectNotifications = false;
        s.Save(sp);

        var back = DovetailSettings.Load(sp);
        Check("second launch: first run completed", back.FirstRunCompleted, true);
        Check("second launch: handover suppressed", back.HandoverExplained, true);
        Check("second launch: auto-start decision kept as no", back.AutoStartOnLogin, false);
        Check("second launch: disconnect notices stay off", back.ShowDisconnectNotifications, false);
        Check("second launch: connect notices still on", back.ShowConnectNotifications, true);

        // A corrupt settings file must not stop the app starting; it falls back to defaults.
        File.WriteAllText(sp, "{ this is not json");
        var salvaged = DovetailSettings.Load(sp);
        Check("corrupt settings file falls back to defaults", salvaged.FirstRunCompleted, false);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 7. migration

    private static void PortFileMigration()
    {
        Console.WriteLine(" 7. MIGRATION - old per-port files become slot files");
        string dir = Path.Combine(Path.GetTempPath(), "dovetail-migrate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var p = new CalibrationProfile { Name = "pad-port1" };
            p.Device.ReportId = 1;
            File.WriteAllText(Path.Combine(dir, "pad-port1.calibration.json"),
                JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));

            var log = SlotRegistry.MigrateFromPortFiles(dir);
            foreach (var line in log) Console.WriteLine("      " + line);
            Ok("slot1 file created", File.Exists(Path.Combine(dir, "slot1.calibration.json")));
            var reg = SlotRegistry.Load(dir);
            Check("migrated profile loads as slot 1", reg.ForSlot(1)?.SlotNumber, 1);
            Ok("the old port file was retired, not left in the profiles folder",
               !File.Exists(Path.Combine(dir, "pad-port1.calibration.json")));
            Ok("its data is recoverable from _superseded",
               File.Exists(Path.Combine(dir, "_superseded", "pad-port1.calibration.json")));
            Check("running migration twice is a no-op", SlotRegistry.MigrateFromPortFiles(dir).Count, 0);
            Check("and the slot file is untouched by the second run",
                  SlotRegistry.Load(dir).ForSlot(1)?.SlotNumber, 1);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 8. auto-start

    private static void AutoStartRoundTrip()
    {
        Console.WriteLine(" 8. AUTO-START - HKCU Run entry, written and removed cleanly");

        // Save and restore whatever is really there, so the test cannot leave the machine
        // set up differently from how it found it.
        string? original = OriginalAutoStart();
        bool wasEnabled = original is not null;
        Console.WriteLine($"      before: {(wasEnabled ? "enabled - " + original : "not present")}");

        try
        {
            string fake = Path.Combine(Path.GetTempPath(), "DovetailAutoStartTest", "Dovetail.exe");
            Check("enable writes the entry", AutoStart.Enable(fake), true);
            Ok("IsEnabled now true", AutoStart.IsEnabled());
            string cmd = AutoStart.CurrentCommand() ?? "";
            Console.WriteLine($"      wrote: {cmd}");
            Ok("command quotes the path", cmd.StartsWith("\""));
            Ok("command carries --tray so it starts silent", cmd.Contains("--tray"));
            Ok("command points at the exe we asked for", cmd.Contains("DovetailAutoStartTest"));

            Check("disable removes it", AutoStart.Disable(), true);
            Check("IsEnabled now false", AutoStart.IsEnabled(), false);
            Check("disable twice is harmless", AutoStart.Disable(), true);
        }
        finally
        {
            if (wasEnabled && original is not null)
            {
                WriteRunValue("Dovetail Gamepad Emulator", original);
                Console.WriteLine($"      restored the original entry: {AutoStart.IsEnabled()}");
            }
            else
            {
                AutoStart.Disable();
                Console.WriteLine("      left with no auto-start entry, as found");
            }
        }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 11. stick feel

    /// <summary>
    /// The four numbers the settings cards now edit, and the guarantee that editing them there
    /// means exactly what editing them from `tune` means.
    ///
    /// Both front ends call <see cref="StickFeel"/>, so what is worth testing is the shared
    /// behaviour: the clamps hold whatever a slider or a command line hands over, a preset
    /// gives each stick the threshold it actually has to clear, and a partial edit leaves the
    /// fields it did not touch alone. The last one is the one that would lose measured data:
    /// a settings window that wrote nulls over centres and ranges would destroy a calibration
    /// while appearing to adjust a deadzone.
    /// </summary>
    private static void StickFeelEditing(string dir)
    {
        Console.WriteLine(" 11. STICK FEEL - one implementation behind the sliders and the tune command");

        string path = SlotRegistry.PathForSlot(dir, 1);
        if (!File.Exists(path)) { Console.WriteLine("      (no slot1 profile to work from, skipped)"); Console.WriteLine(); return; }

        // ---- clamping, at and beyond both ends ----
        Check("a deadzone below the floor is clamped", StickFeel.ClampDeadzone(-5), StickFeel.MinDeadzone);
        Check("and above the ceiling", StickFeel.ClampDeadzone(9999), StickFeel.MaxDeadzone);
        Check("a curve below the floor is clamped", StickFeel.ClampCurve(0.01), StickFeel.MinCurve);
        Check("and above the ceiling", StickFeel.ClampCurve(50), StickFeel.MaxCurve);
        Check("saturation cannot exceed the 8-bit mechanical limit",
              StickFeel.ClampSaturation(500), 127);
        Check("anti-deadzone cannot go negative", StickFeel.ClampAntiDeadzone(-1), 0);

        // ---- a preset gives each stick its own floor ----
        var standard = StickFeel.FindPreset("standard");
        Ok("the standard preset exists", standard is not null);
        Check("the left floor clears XInput's left thumb deadzone",
              standard!.LeftFloor > XInputMapper.XInputLeftThumbDeadzone, true);
        Check("the right floor clears the right one, which is higher",
              standard.RightFloor > XInputMapper.XInputRightThumbDeadzone, true);
        Ok("and the two are not the same number, because the thresholds are not",
           standard.LeftFloor != standard.RightFloor);
        Check("an unknown preset id is null rather than a throw", StickFeel.FindPreset("nope"), null);

        var applied = CalibrationProfile.Load(path);
        int centreBefore = applied.Axes["leftStickX"].Centre;
        int minBefore = applied.Axes["leftStickX"].Min;
        int maxBefore = applied.Axes["leftStickX"].Max;

        StickFeel.Apply(applied, StickFeel.Settings.FromPreset(standard));
        Check("the preset reached the left stick",
              applied.Axes["leftStickX"].AntiDeadzone, standard.LeftFloor);
        Check("and the right stick got the right stick's floor",
              applied.Axes["rightStickX"].AntiDeadzone, standard.RightFloor);
        Check("curve applied", applied.Axes["leftStickX"].ResponseCurve, standard.Curve);
        Check("saturation applied", applied.Axes["leftStickX"].Saturation, standard.Saturation);

        Check("THE MEASURED CENTRE IS UNTOUCHED", applied.Axes["leftStickX"].Centre, centreBefore);
        Check("AND SO IS THE MEASURED RANGE",
              $"{applied.Axes["leftStickX"].Min}..{applied.Axes["leftStickX"].Max}",
              $"{minBefore}..{maxBefore}");

        // ---- a partial edit, which is what one slider produces ----
        var partial = CalibrationProfile.Load(path);
        StickFeel.Apply(partial, StickFeel.Settings.FromPreset(standard));
        double curveBefore = partial.Axes["leftStickX"].ResponseCurve;
        int satBefore = partial.Axes["leftStickX"].Saturation;

        StickFeel.Apply(partial, new StickFeel.Settings { Deadzone = 11 });
        Check("moving one slider writes that field", partial.Axes["leftStickX"].Deadzone, 11);
        Check("and leaves the curve alone", partial.Axes["leftStickX"].ResponseCurve, curveBefore);
        Check("and the saturation", partial.Axes["leftStickX"].Saturation, satBefore);
        Check("and the anti-deadzone", partial.Axes["leftStickX"].AntiDeadzone, standard.LeftFloor);

        // ---- an out-of-range value handed over by a caller is clamped on the way in ----
        StickFeel.Apply(partial, new StickFeel.Settings { Deadzone = 9999, ResponseCurve = 0.001 });
        Check("a wild deadzone lands clamped in the profile",
              partial.Axes["leftStickX"].Deadzone, StickFeel.MaxDeadzone);
        Check("and a wild curve", partial.Axes["leftStickX"].ResponseCurve, StickFeel.MinCurve);

        // ---- the note, which must replace rather than accumulate ----
        StickFeel.Note(partial, "standard", "settings window");
        StickFeel.Note(partial, "precise", "tune");
        Check("re-tuning replaces the note rather than stacking them",
              partial.Notes.Count(n => n.StartsWith(StickFeel.NotePrefix, StringComparison.Ordinal)), 1);
        Ok("and the surviving note names the latest source",
           partial.Notes.Any(n => n.Contains("tune") && n.Contains("precise")));

        // ---- the figure the card puts in front of the user ----
        var axis = applied.Axes["leftStickX"];
        int travel = StickFeel.UsableTravelPercent(axis, rightStick: false);
        Console.WriteLine($"      usable travel with 'standard': {travel}%");
        Ok("usable travel is a percentage", travel is >= 0 and <= 100);

        var off = CalibrationProfile.Load(path);
        StickFeel.Apply(off, StickFeel.Settings.FromPreset(StickFeel.FindPreset("off")!));
        int travelOff = StickFeel.UsableTravelPercent(off.Axes["leftStickX"], false);
        Console.WriteLine($"      usable travel with 'off':      {travelOff}%");
        Ok("AND THE PRESET MEASURABLY IMPROVES IT, which is the whole point of the card",
           travel > travelOff);

        // ---- round trip through the file, because the card saves and the engine re-reads ----
        string scratch = Path.Combine(dir, "feel-roundtrip.calibration.json");
        applied.Save(scratch);
        var reread = CalibrationProfile.Load(scratch);
        Check("anti-deadzone survives the save and reload",
              reread.Axes["leftStickX"].AntiDeadzone, standard.LeftFloor);
        Check("so does the curve", reread.Axes["leftStickX"].ResponseCurve, standard.Curve);
        try { File.Delete(scratch); } catch { }

        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 12. dependencies

    /// <summary>
    /// The Section 5.7 contract, in the parts that can be checked without installing a driver.
    /// The parts that cannot - a real fresh-state install, repair and uninstall - are covered
    /// by the Windows Sandbox run, because there is no honest way to test them on a machine
    /// that already has both drivers.
    /// </summary>
    private static void DependencyContract()
    {
        Console.WriteLine(" 12. DEPENDENCIES - what is required, and which release would be fetched");

        var mgr = new DependencyManager();
        var all = mgr.DetectAll();

        var vigem = all.FirstOrDefault(d => d.Id == DependencyManager.ViGEmBusId);
        var hid = all.FirstOrDefault(d => d.Id == DependencyManager.HidHideId);
        Ok("both dependencies are known", vigem is not null && hid is not null);

        Check("ViGEmBus is required", vigem!.Required, true);

        // The label this whole section exists to correct. Findings Log 4.1: with HidHide inert
        // Sekiro bound to the raw pad through DirectInput and ignored Dovetail completely, while
        // the engine was verifiably translating. "Optional" described a theory; this describes
        // what happened on the first real game the project tested.
        Check("HIDHIDE IS REQUIRED, not optional", hid!.Required, true);
        Ok("and says why, naming the failure rather than the mechanism",
           hid.WhyItIsNeeded.Contains("Required", StringComparison.OrdinalIgnoreCase) &&
           hid.WhyItIsNeeded.Contains("DirectInput", StringComparison.OrdinalIgnoreCase));

        // ---- resolution ----
        var pinnedV = mgr.ResolveLatestAsync(DependencyManager.ViGEmBusId, preferPinned: true)
                         .GetAwaiter().GetResult();
        Ok("--pinned gives the known-good build", pinnedV.Pinned);
        Ok("whose url is the vendor's own release asset",
           pinnedV.Url.StartsWith("https://github.com/nefarius/", StringComparison.OrdinalIgnoreCase));

        foreach (var id in new[] { DependencyManager.ViGEmBusId, DependencyManager.HidHideId })
        {
            var live = mgr.ResolveLatestAsync(id).GetAwaiter().GetResult();
            Console.WriteLine($"      {id}: {live.Tag}  {(live.Pinned ? "(pinned fallback)" : "(live)")}");

            // Always true, online or off: whatever is resolved must come from the vendor.
            Ok($"{id} resolves to a nefarius release asset",
               live.Url.StartsWith("https://github.com/nefarius/", StringComparison.OrdinalIgnoreCase));
            Ok($"{id} resolves to an installer, not to a page",
               live.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            // A machine with no network legitimately falls back, so this reports rather than
            // fails. A test that goes red when the build agent is offline teaches people to
            // ignore it.
            if (live.Pinned)
                Console.WriteLine($"      (offline or API unavailable; the pinned fallback is the correct answer here)");
            else
                Ok($"{id} live tag parses as a version", live.Tag.TrimStart('v', 'V').Split('.').Length >= 2);
        }

        // ---- the recommendation the charter asks this code to state ----
        Ok("the delivery recommendation covers latest-versus-pinned, not just bundle-versus-fetch",
           DependencyManager.DeliveryRecommendation.Contains("Latest versus pinned") &&
           DependencyManager.DeliveryRecommendation.Contains("--pinned"));

        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 13. HidHide ownership

    /// <summary>
    /// The rule that decides what an uninstall undoes: Dovetail reverses the HidHide settings
    /// it made and nothing else.
    ///
    /// This is the sharp edge of requirement 5. Undo too much and another application's hidden
    /// device is exposed; undo too little and the user's pad stays invisible after Dovetail is
    /// gone, with its allow-list entries just removed. The record in <see cref="HidHideState"/>
    /// is what separates the two, so it is tested directly rather than inferred from a run.
    /// </summary>
    private static void HidHideOwnership(string dir)
    {
        Console.WriteLine(" 13. HIDHIDE OWNERSHIP - an uninstall undoes only what setup changed");

        string statePath = Path.Combine(dir, "hidhide-state.json");
        try
        {
            var fresh = HidHideState.Load(statePath);
            Ok("a machine with no record has nothing to undo", !fresh.AnythingRecorded);

            // A configure run that changed nothing must record nothing, or an uninstall would
            // later un-hide a device the user had hidden themselves.
            var noop = new HidHideAccess.Applied();
            fresh.Merge(noop);
            Ok("a no-op configure records nothing", !fresh.AnythingRecorded);

            var applied = new HidHideAccess.Applied
            {
                DevicesHidden = { @"HID\VID_0810&PID_0001&Col01\7&test&0&0000" },
                CloakTurnedOn = true,
            };
            fresh.Merge(applied);
            Check("a configure that hid a device records it", fresh.DevicesWeHid.Count, 1);
            Ok("and records that it turned the cloak on", fresh.WeTurnedCloakOn);
            Ok("with a timestamp", fresh.LastConfiguredLocal.Length > 0);

            // Setup runs more than once: a second controller, or a repair. The record has to
            // accumulate rather than replace, or the first device stops being ours.
            fresh.Merge(new HidHideAccess.Applied
            {
                DevicesHidden = { @"HID\VID_0810&PID_0001&Col02\7&test&0&0001" },
            });
            Check("a later run adds to the record rather than replacing it", fresh.DevicesWeHid.Count, 2);
            fresh.Merge(new HidHideAccess.Applied
            {
                DevicesHidden = { @"HID\VID_0810&PID_0001&Col02\7&test&0&0001" },
            });
            Check("and the same device twice is recorded once", fresh.DevicesWeHid.Count, 2);

            Ok("the record saves", fresh.Save(statePath));
            var reread = HidHideState.Load(statePath);
            Check("and survives a reload", reread.DevicesWeHid.Count, 2);
            Ok("including the cloak flag", reread.WeTurnedCloakOn);

            // The survey must only offer to un-hide devices that are actually hidden right now.
            // These two ids are invented, so nothing on this machine matches them, and the
            // correct answer is "nothing to undo" even though the record is not empty.
            var plan = new UninstallCleanup(dir, dir, statePath).Survey();
            Check("a recorded device that is not actually hidden is not offered for un-hiding",
                  plan.HidHideDevicesToUnhide.Count, 0);
            Ok("and the plan does not claim it will change the cloak for them",
               plan.WillTurnCloakOff == (reread.WeTurnedCloakOn && HidHideAccess.Cloaking()));

            // Found by running the round trip twice on real hardware. HidHide stores whatever
            // spelling it is given, verbatim, and matches it CASE-SENSITIVELY. Windows'
            // enumeration and HidHide's own client disagree about case for the same device:
            //   enumerated : HID\VID_0810&PID_0001&COL02\7&744114A&0&0001
            //   HidHide's  : HID\VID_0810&PID_0001&Col01\7&744114a&0&0000
            // So --dev-unhide with the enumerated spelling silently does nothing to an entry
            // the user's client wrote, and --dev-hide with it adds a second entry for a device
            // already hidden. Both report success. Every comparison here is case-insensitive
            // and every CLI call is handed HidHide's own spelling; these are the real strings.
            const string enumerated = @"HID\VID_0810&PID_0001&COL02\7&744114A&0&0001";
            const string asHidHideWroteIt = @"HID\VID_0810&PID_0001&Col02\7&744114a&0&0001";
            Ok("THE SAME DEVICE IS RECOGNISED ACROSS THE TWO SPELLINGS",
               HidHideAccess.SameDeviceId(enumerated, asHidHideWroteIt));
            Ok("and it is not simply matching everything",
               !HidHideAccess.SameDeviceId(enumerated, @"HID\VID_0810&PID_0001&COL01\7&744114A&0&0000"));

            Ok("the record deletes", HidHideState.Delete(statePath));
            Ok("and is gone", !File.Exists(statePath));
            Ok("loading a deleted record is empty, not an error",
               !HidHideState.Load(statePath).AnythingRecorded);
        }
        finally { try { File.Delete(statePath); } catch { } }

        Console.WriteLine();
    }

    /// <summary>
    /// The auto-start entry to put back after a test. An entry naming the tests' own fake
    /// executable is not the user's: it is what an interrupted run left behind, and restoring it
    /// would keep Windows launching a missing file at every sign-in. Treated as absent.
    /// </summary>
    private static string? OriginalAutoStart()
    {
        string? cmd = AutoStart.CurrentCommand();
        if (cmd is null || !cmd.Contains("DovetailAutoStartTest", StringComparison.OrdinalIgnoreCase)) return cmd;
        Console.WriteLine($"      ignoring a leftover test entry: {cmd}");
        return null;
    }

    private static void WriteRunValue(string name, string command) =>
        Microsoft.Win32.Registry.SetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", name, command);

    // ------------------------------------------------------------------ 9. profile storage

    /// <summary>
    /// Where profiles live after the packaging move, and the one-time migration that gets
    /// them there.
    ///
    /// The migration runs against a real folder beside this executable rather than a mock,
    /// because the thing being tested is a file copy and the defects worth catching are file
    /// defects: a second run that overwrites an edited calibration, a marker that is written
    /// before the copy, a retired profile left behind in _superseded.
    /// </summary>
    private static void ProfileStorage()
    {
        Console.WriteLine(" 9. PROFILE STORAGE - per-user folder, migrated once from the install folder");

        string canonical = ProfileStore.CanonicalDirectory;
        Console.WriteLine($"      canonical: {canonical}");
        Ok("the canonical folder is under LocalApplicationData",
           canonical.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                StringComparison.OrdinalIgnoreCase));
        Ok("it is %LOCALAPPDATA%\\Dovetail\\profiles",
           canonical.EndsWith(Path.Combine("Dovetail", "profiles"), StringComparison.OrdinalIgnoreCase));

        // This test binary lives in src\Dovetail.Engine\bin\..., so it is a development tree
        // and must be recognised as one.
        string? dev = ProfileStore.DevelopmentTree();
        Console.WriteLine($"      development tree: {dev ?? "(none)"}");
        Ok("a build output under src is recognised as a development tree", dev is not null);
        Ok("and the resolver hands back that folder rather than the per-user one",
           dev is null || string.Equals(ProfileStore.Resolve(), dev, StringComparison.OrdinalIgnoreCase));

        // ---- the migration itself ----
        string seed = ProfileStore.BesideExecutable;      // profiles\ beside this exe
        string target = Path.Combine(Path.GetTempPath(), "dovetail-store-" + Guid.NewGuid().ToString("N")[..8]);
        bool seedWasThere = Directory.Exists(seed);

        try
        {
            Directory.CreateDirectory(seed);
            File.WriteAllText(Path.Combine(seed, "slot1.calibration.json"), "{\"name\":\"seed\"}");
            File.WriteAllText(Path.Combine(seed, "settings.json"), "{\"SchemaVersion\":1}");
            Directory.CreateDirectory(Path.Combine(seed, "_superseded"));
            File.WriteAllText(Path.Combine(seed, "_superseded", "pad-port1.calibration.json"), "{\"old\":true}");

            foreach (var line in ProfileStore.Migrate(target)) Console.WriteLine("      " + line);

            Ok("the calibration was copied", File.Exists(Path.Combine(target, "slot1.calibration.json")));
            Ok("so were the settings", File.Exists(Path.Combine(target, "settings.json")));
            Ok("and the retired profiles in _superseded, which are measured data too",
               File.Exists(Path.Combine(target, "_superseded", "pad-port1.calibration.json")));
            Ok("the marker was written", File.Exists(Path.Combine(target, ProfileStore.MigrationMarker)));
            Ok("the install folder was left alone, so a re-install still has its seed",
               File.Exists(Path.Combine(seed, "slot1.calibration.json")));

            // The defect this guards: a second migration overwriting live calibrations with
            // the installer's stale seed copy.
            File.WriteAllText(Path.Combine(target, "slot1.calibration.json"), "{\"name\":\"edited by the user\"}");
            foreach (var line in ProfileStore.Migrate(target)) Console.WriteLine("      " + line);
            Check("a second migration does not overwrite an edited calibration",
                  File.ReadAllText(Path.Combine(target, "slot1.calibration.json")),
                  "{\"name\":\"edited by the user\"}");

            // And the marker, not the directory, is what stops it running again: a crash log
            // can create the directory before any profile exists.
            string bare = Path.Combine(Path.GetTempPath(), "dovetail-bare-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(bare);
            File.WriteAllText(Path.Combine(bare, "dovetail-errors.log"), "a crash got here first");
            ProfileStore.Migrate(bare);
            Ok("a directory that exists without the marker still migrates",
               File.Exists(Path.Combine(bare, "slot1.calibration.json")));
            try { Directory.Delete(bare, true); } catch { }
        }
        finally
        {
            try { Directory.Delete(target, true); } catch { }
            if (!seedWasThere) { try { Directory.Delete(seed, true); } catch { } }
        }
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ 10. uninstall

    /// <summary>
    /// The uninstaller, including the part that can break software this project did not write.
    ///
    /// The allow-list rule is checked against a list holding DS4Windows and Steam entries,
    /// because "removes only Dovetail's entries" is the claim that matters and an off-by-one
    /// in it takes another product's controller access away. The removal and restore are run
    /// for real against scratch folders, which is why <see cref="UninstallCleanup"/> takes its
    /// two directories as arguments.
    /// </summary>
    private static void UninstallCleanupRoundTrip()
    {
        Console.WriteLine(" 10. UNINSTALL - reversible, ours only, and it asks before touching profiles");

        // ---- the allow-list rule, against a realistic list ----
        var list = new List<string>
        {
            @"C:\Program Files\DS4Windows\DS4Windows.exe",
            @"C:\Program Files (x86)\Steam\steam.exe",
            @"C:\Program Files\Dovetail\Dovetail.exe",
            @"C:\Program Files\Dovetail\dovetail-engine.exe",
            @"C:\Users\someone\Desktop\old build\dovetail-diag.exe",
            @"C:\Program Files\Some Vendor\dovetail-notours.exe",
        };
        var ours = UninstallCleanup.FilterOurs(list);
        Check("our own executables are selected", ours.Count, 3);
        Ok("Dovetail.exe is ours", ours.Any(p => p.EndsWith(@"Dovetail\Dovetail.exe")));
        Ok("an entry from another folder is ours too, so a moved install is cleaned up",
           ours.Any(p => p.EndsWith(@"old build\dovetail-diag.exe")));
        Ok("DS4WINDOWS IS NOT TOUCHED", !ours.Any(p => p.Contains("DS4Windows")));
        Ok("STEAM IS NOT TOUCHED", !ours.Any(p => p.Contains("steam.exe")));
        Ok("nor is a file that merely starts with our name",
           !ours.Any(p => p.Contains("dovetail-notours")));

        // ---- removal and restore, for real, against scratch folders ----
        string profiles = Path.Combine(Path.GetTempPath(), "dovetail-uninst-p-" + Guid.NewGuid().ToString("N")[..8]);
        string restore = Path.Combine(Path.GetTempPath(), "dovetail-uninst-r-" + Guid.NewGuid().ToString("N")[..8]);
        string? original = OriginalAutoStart();

        try
        {
            Directory.CreateDirectory(profiles);
            File.WriteAllText(Path.Combine(profiles, "slot1.calibration.json"), "{\"measured\":true}");
            File.WriteAllText(Path.Combine(profiles, "settings.json"), "{\"SchemaVersion\":1}");

            AutoStart.Disable();
            string fake = Path.Combine(Path.GetTempPath(), "DovetailAutoStartTest", "Dovetail.exe");
            AutoStart.Enable(fake);

            var cleanup = new UninstallCleanup(profiles, restore);

            var plan = cleanup.Survey();
            Ok("the survey finds the auto-start entry", plan.AutoStartCommand is not null);
            Check("and counts the profile files", plan.ProfileFileCount, 2);
            Ok("the survey changed nothing", AutoStart.IsEnabled() && File.Exists(Path.Combine(profiles, "slot1.calibration.json")));

            // Keeping profiles is the default answer, and it has to be honoured exactly.
            var kept = cleanup.Run(removeProfiles: false);
            Ok("with profiles kept, the auto-start entry still goes", !AutoStart.IsEnabled());
            Ok("AND THE CALIBRATIONS ARE STILL THERE",
               File.Exists(Path.Combine(profiles, "slot1.calibration.json")));
            Ok("a restore point was written", kept.RestorePointPath is not null && File.Exists(kept.RestorePointPath));

            var restored = cleanup.Restore(kept.RestorePointPath!);
            Ok("restoring puts the auto-start entry back", AutoStart.IsEnabled());
            Check("and the command is the one that was removed", AutoStart.CurrentCommand(), $"\"{fake}\" --tray");
            Ok("with no problems reported", restored.Problems.Count == 0);

            // Now the destructive answer, which still must not actually destroy anything.
            var removed = cleanup.Run(removeProfiles: true);
            Ok("with profiles removed, the folder is gone from where the app reads it",
               !Directory.Exists(profiles));
            Ok("but they were moved, not deleted", removed.Removed.ProfilesMovedTo is not null
               && File.Exists(Path.Combine(removed.Removed.ProfilesMovedTo!, "slot1.calibration.json")));

            var back = new UninstallCleanup(profiles, restore).Restore(removed.RestorePointPath!);
            Ok("and restoring brings the calibrations back to where they were",
               File.Exists(Path.Combine(profiles, "slot1.calibration.json")));
            Check("with their contents intact",
                  File.ReadAllText(Path.Combine(profiles, "slot1.calibration.json")), "{\"measured\":true}");
            Ok("restore reported no problems", back.Problems.Count == 0);

            Ok("the latest restore point is findable without being told the file name",
               UninstallCleanup.LatestRestorePoint(restore) is not null);
        }
        finally
        {
            AutoStart.Disable();
            if (original is not null) WriteRunValue("Dovetail Gamepad Emulator", original);
            try { Directory.Delete(profiles, true); } catch { }
            try { Directory.Delete(restore, true); } catch { }
            Console.WriteLine("      scratch folders removed, registry left as found");
        }
        Console.WriteLine();
    }
}
