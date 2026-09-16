using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// The command an uninstaller runs, and the one a user can run by hand to see what Dovetail
/// left on their machine.
///
/// It lives in the engine rather than in the tray app for the same reason dependency repair
/// does: this is the elevated helper, editing HidHide's allow list needs administrator rights,
/// and a console tool can be driven by an installer's silent mode without a window appearing.
///
/// **The profiles question is asked, never assumed.** Interactively it is a prompt. Driven by
/// an installer it is <c>--remove-profiles</c> or <c>--keep-profiles</c>, whichever the
/// installer's own checkbox produced. With no answer available at all - no flag, no console to
/// ask on - the profiles are kept. Keeping data the user did not ask to lose is recoverable;
/// deleting a calibration they wanted is a full input sweep per pad to put right.
/// </summary>
internal static class Uninstall
{
    internal static int Run(string[] args)
    {
        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "plan";
        return sub switch
        {
            "plan" => Show(),
            "run" => Execute(args),
            "restore" => RestoreRun(args),
            _ => Usage($"unknown 'uninstall' subcommand '{sub}'"),
        };
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine(problem);
        Console.Error.WriteLine("""
              uninstall plan              show what would be removed, change nothing
              uninstall run               remove it, asking about calibration profiles
                  --remove-profiles       answer the profiles question yes, no prompt
                  --keep-profiles         answer it no, no prompt
                  --yes                   skip the final confirmation as well
              uninstall restore [<file>]  put back what a cleanup removed
                                          (defaults to the most recent restore point)
            """);
        return 1;
    }

    private static void Banner(string title)
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" " + title);
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ plan

    private static int Show()
    {
        Banner("DOVETAIL - WHAT AN UNINSTALL WOULD REMOVE");
        var plan = new UninstallCleanup().Survey();
        PrintPlan(plan);

        if (!plan.AnythingToDo)
        {
            Console.WriteLine(" Nothing of Dovetail's is on this machine.");
            return 0;
        }

        Console.WriteLine(" Nothing was changed. Run 'dovetail-engine uninstall run' to do it.");
        return 0;
    }

    private static void PrintPlan(UninstallCleanup.Plan plan)
    {
        Console.WriteLine(" WOULD REMOVE");
        Console.WriteLine();

        Console.WriteLine(" 1. Auto-start entry, HKCU Run");
        if (plan.AutoStartCommand is null)
            Console.WriteLine("      (none present)");
        else
            Console.WriteLine($"      \"Dovetail Gamepad Emulator\" = {plan.AutoStartCommand}");
        Console.WriteLine("      The value only. Every other startup entry is left alone.");
        Console.WriteLine();

        Console.WriteLine(" 2. HidHide, only what Dovetail itself put there");
        if (!plan.HidHideInstalled)
            Console.WriteLine("      HidHide is not installed, so there is nothing to undo.");
        else
        {
            Console.WriteLine("    allow-list entries:");
            if (plan.HidHideEntries.Count == 0) Console.WriteLine("      (nothing of ours is listed)");
            foreach (var e in plan.HidHideEntries) Console.WriteLine($"      {e}");

            Console.WriteLine("    device hiding Dovetail set up:");
            if (plan.HidHideDevicesToUnhide.Count == 0)
                Console.WriteLine("      (none recorded as ours)");
            foreach (var d in plan.HidHideDevicesToUnhide) Console.WriteLine($"      un-hide {d}");

            Console.WriteLine(plan.WillTurnCloakOff
                ? "    cloaking: turned back off, because Dovetail turned it on"
                : "    cloaking: left as it is");

            if (plan.NeedsElevationForHidHide)
                Console.WriteLine("      NOTE: this needs administrator rights. Re-run elevated.");
        }
        Console.WriteLine("      Entries and hidden devices belonging to other software are left");
        Console.WriteLine("      exactly as they are. Undoing our own hiding matters: leaving it");
        Console.WriteLine("      would keep the pad invisible with Dovetail no longer allowed.");
        Console.WriteLine();

        Console.WriteLine(" 3. Calibration profiles  -  ONLY IF YOU SAY SO");
        Console.WriteLine($"      {(plan.ProfileDirectory.Length > 0 ? plan.ProfileDirectory : "(no per-user folder available)")}");
        Console.WriteLine(plan.ProfilesExist
            ? $"      {plan.ProfileFileCount} file(s). These are measured from your hardware and"
            : "      (empty, nothing to remove)");
        if (plan.ProfilesExist)
            Console.WriteLine("      take a full input sweep per pad to recreate.");
        Console.WriteLine();

        Console.WriteLine(" LEFT INSTALLED");
        Console.WriteLine();
        Console.WriteLine("      ViGEmBus and HidHide. Both are separate products that other");
        Console.WriteLine("      software uses - DS4Windows, Steam and x360ce all drive ViGEmBus -");
        Console.WriteLine("      so removing them here would break whatever else depends on them.");
        Console.WriteLine("      Remove them from Settings > Apps > Installed apps if nothing");
        Console.WriteLine("      else on this machine needs them.");
        Console.WriteLine();
    }

    // ------------------------------------------------------------------ run

    private static int Execute(string[] args)
    {
        Banner("DOVETAIL - UNINSTALL CLEANUP");

        var cleanup = new UninstallCleanup();
        var plan = cleanup.Survey();
        PrintPlan(plan);

        if (!plan.AnythingToDo)
        {
            Console.WriteLine(" Nothing of Dovetail's is on this machine. Nothing to do.");
            return 0;
        }

        bool removeFlag = HasFlag(args, "--remove-profiles");
        bool keepFlag = HasFlag(args, "--keep-profiles");
        if (removeFlag && keepFlag)
        {
            Console.Error.WriteLine(" --remove-profiles and --keep-profiles cannot both be given.");
            return 1;
        }

        bool removeProfiles;
        if (removeFlag) { removeProfiles = true; Console.WriteLine(" profiles: removal requested on the command line"); }
        else if (keepFlag) { removeProfiles = false; Console.WriteLine(" profiles: keeping, requested on the command line"); }
        else if (!plan.ProfilesExist) removeProfiles = false;
        else removeProfiles = AskAboutProfiles(plan);

        if (!HasFlag(args, "--yes") && Interactive())
        {
            Console.WriteLine();
            Console.Write(" Go ahead with the cleanup above? [y/N] ");
            if (!YesTyped())
            {
                Console.WriteLine();
                Console.WriteLine(" Nothing was changed.");
                return 1;
            }
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 74));
        var result = cleanup.Run(removeProfiles, m => Console.WriteLine("  " + m));
        Console.WriteLine(new string('-', 74));
        Console.WriteLine();

        if (result.Success) Console.WriteLine(" Cleanup finished. Dovetail is off this machine.");
        else
        {
            Console.WriteLine(" Cleanup finished with problems:");
            foreach (var p in result.Problems) Console.WriteLine("   - " + p);
        }

        if (result.RestorePointPath is { } rp)
        {
            Console.WriteLine();
            Console.WriteLine(" This is reversible. To put it all back:");
            Console.WriteLine($"   dovetail-engine uninstall restore \"{rp}\"");
        }
        return result.Success ? 0 : 3;
    }

    /// <summary>
    /// The one destructive choice, put to the user in their own terms. Defaults to keeping:
    /// a bare Enter, a redirected stdin, or an installer that forgot the flag all leave the
    /// calibrations where they are.
    /// </summary>
    private static bool AskAboutProfiles(UninstallCleanup.Plan plan)
    {
        if (!Interactive())
        {
            Console.WriteLine(" profiles: no answer given and nothing to ask on, so they are kept.");
            Console.WriteLine($"           They stay at {plan.ProfileDirectory}");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine(" Your calibration profiles are the measurements taken from your own");
        Console.WriteLine(" controllers. Keeping them means a reinstall picks up where you left");
        Console.WriteLine(" off. Removing them means calibrating every pad again from scratch.");
        Console.WriteLine();
        Console.Write($" Remove the {plan.ProfileFileCount} calibration file(s) as well? [y/N] ");
        bool yes = YesTyped();
        Console.WriteLine(yes
            ? " They will be moved to the restore folder, not deleted outright."
            : " Keeping them.");
        return yes;
    }

    // ------------------------------------------------------------------ restore

    private static int RestoreRun(string[] args)
    {
        Banner("DOVETAIL - RESTORE A CLEANUP");

        string? path = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : UninstallCleanup.LatestRestorePoint();
        if (path is null)
        {
            Console.Error.WriteLine($" No restore point found in {UninstallCleanup.DefaultRestoreDirectory}");
            return 1;
        }
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($" No such restore point: {path}");
            return 1;
        }

        Console.WriteLine($" restore point: {path}");
        Console.WriteLine();

        var result = new UninstallCleanup().Restore(path, m => Console.WriteLine("  " + m));
        Console.WriteLine();
        if (result.Success) Console.WriteLine(" Restored.");
        else
        {
            Console.WriteLine(" Restored with problems:");
            foreach (var p in result.Problems) Console.WriteLine("   - " + p);
        }
        return result.Success ? 0 : 3;
    }

    // ------------------------------------------------------------------ plumbing

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether there is a person on the other end. An installer runs this with stdin
    /// redirected, and a prompt nobody can answer would either hang the install or read EOF
    /// and be taken as an answer the user never gave.
    /// </summary>
    private static bool Interactive() => !Console.IsInputRedirected;

    private static bool YesTyped()
    {
        string? line = Console.ReadLine();
        return line is not null && line.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }
}
