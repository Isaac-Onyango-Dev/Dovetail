using System.Text;
using Dovetail.Core;

namespace Dovetail.Engine;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            return cmd switch
            {
                "run" => Runner.Run(args),
                "validate" => Validator.Run(args),
                "slots" => Slots(args),
                "deps" => Deps.Run(args),
                "uninstall" => Uninstall.Run(args),
                "selftest" => SelfTest.Run(args),
                "stage5" => Stage5Test.Run(args),
                "tune" => Tune.Run(args),
                "burst" => BurstTest.Run(args),
                "multipad" => MultiPad.Run(args),
                "playtest" => PlayTest.Run(args),
                "verify-device" => VerifyDevice.Run(args),
                "-h" or "--help" or "help" => Usage(),
                _ => Usage($"unknown command '{cmd}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static int Usage(string? problem = null)
    {
        if (problem is not null) Console.Error.WriteLine($"{problem}\n");
        Console.WriteLine("""
            dovetail-engine - Dovetail translation engine (Sections 5.2 and 5.3)

              run      [--profile <file>] [--seconds N] [--quiet]
                       Translate the physical pad onto a virtual Xbox 360 controller.

              validate [--profile <file>] [--out <file>] [--step-timeout N]
                       Start the engine, then read XInput back from the OS and confirm every
                       input arrives. This is the Stage 3 exit check.

              slots    Show what XInput reports in each of the four slots right now.

              verify-device
                       Creates a virtual pad and round-trips a value through XInput.
                       Needs no physical controller, so it runs in a clean sandbox.

              deps status | install <id> | repair <id> | verify <file> | recommendation
                       Section 5.7 dependency setup: detect, fetch, verify, install.

              deps paths
                       Where this build reads and writes profiles, and why.

              uninstall plan | run | restore [<file>]
                       Remove what Dovetail put on this machine: the auto-start entry and
                       its own HidHide allow-list entries, and calibration profiles only
                       if you say so. ViGEmBus and HidHide are left installed. Every run
                       writes a restore point, so it can all be put back.

              selftest Hardware-free checks on the decoder and the axis scaling maths.
              stage5   Hardware-free checks on slot identity, naming, forgetting,
                       per-game overrides, first-run gating and auto-start.

              playtest [--minutes N] [--game <name>] [--out <file>]
                       Stage 4 in-game checklist. Records which inputs reach the game
                       while you play.

              multipad [--step-timeout N] [--out <file>]
                       Proves two pads drive two independent XInput players.

              burst    [--seconds N] [--out <file>]
                       Measures whether rapid presses are lost, and the end-to-end
                       latency from HID report to visible in XInput.

              tune     [--show] [--preset off|standard|precise|responsive|full-range]
                       [--anti-deadzone N] [--curve C] [--deadzone N]
                       Stick feel. A running engine reloads within a second.

            Profiles live in %LOCALAPPDATA%\Dovetail\profiles as slot1.calibration.json,
            slot2... one per player. Running from a build tree uses the repository's own
            profiles folder instead; 'deps paths' prints which one is in use.
            """);
        return problem is null ? 0 : 1;
    }

    /// <summary>
    /// %LOCALAPPDATA%\Dovetail\profiles for an installed build, the repository's own folder
    /// when this is running from a build tree. <see cref="ProfileStore"/> owns that decision
    /// and the one-time migration behind it; every tool in the solution goes through it, so
    /// the wizard and the tray app can never end up looking at different folders.
    /// </summary>
    internal static string DefaultProfileDirectory() => ProfileStore.Resolve();

    internal static string DefaultProfilePath() =>
        Path.Combine(DefaultProfileDirectory(), SlotRegistry.FileNameForSlot(1));

    internal static string ProfileDirectory(string[] args)
    {
        var explicitPath = ArgValue(args, "--profile");
        if (explicitPath is null) return DefaultProfileDirectory();
        // Accept either a directory or a file inside one.
        return Directory.Exists(explicitPath)
            ? explicitPath
            : Path.GetDirectoryName(explicitPath) ?? DefaultProfileDirectory();
    }

    internal static SlotRegistry LoadSlots(string[] args, Action<string> log)
    {
        string dir = ProfileDirectory(args);

        var migrated = SlotRegistry.MigrateFromPortFiles(dir);
        foreach (var line in migrated) log("migrated: " + line);

        var reg = SlotRegistry.Load(dir);
        log($"profile directory: {dir}");
        foreach (var line in reg.LoadLog) log("  " + line);
        return reg;
    }

    internal static CalibrationProfile LoadProfile(string[] args, Action<string> log)
    {
        string path = ArgValue(args, "--profile") ?? DefaultProfilePath();
        if (!File.Exists(path))
            throw new FileNotFoundException($"calibration profile not found at {path}. Run 'dovetail-diag calibrate' first.");
        var p = CalibrationProfile.Load(path);
        log($"profile: {path}");
        log($"  device {p.Device.Product} {p.Device.Vid}/{p.Device.Pid}, calibrated {p.Device.CollectionTag} " +
            $"report id {p.Device.ReportId}, created {p.Device.RestState}");
        return p;
    }

    private static int Slots(string[] args)
    {
        var reader = XInputReader.OpenFirstAvailable(out var tried);
        foreach (var t in tried) Console.WriteLine("  " + t);
        if (reader is null) { Console.Error.WriteLine("no XInput runtime could be loaded"); return 1; }

        Console.WriteLine();
        Console.WriteLine($"using {reader.DllName}");
        Console.WriteLine();
        for (int i = 0; i < 4; i++)
        {
            var s = reader.Read(i);
            Console.WriteLine(s is null
                ? $"  slot {i}: empty"
                : $"  slot {i}: CONNECTED  buttons=0x{s.Value.Buttons:X4} [{XInputButtons.Describe(s.Value.Buttons)}] " +
                  $"LT={s.Value.LeftTrigger} RT={s.Value.RightTrigger} " +
                  $"L({s.Value.ThumbLX},{s.Value.ThumbLY}) R({s.Value.ThumbRX},{s.Value.ThumbRY})");
        }
        reader.Dispose();
        return 0;
    }

    internal static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
