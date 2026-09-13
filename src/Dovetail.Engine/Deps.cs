using Dovetail.Core;

namespace Dovetail.Engine;

/// <summary>
/// Command-line face of the Section 5.7 dependency setup. The same
/// <see cref="DependencyManager"/> drives the Stage 5 first-run screen and its
/// "Repair Dependencies" tray entry, so the logic is exercised here first and the UI later
/// is only presentation.
/// </summary>
internal static class Deps
{
    internal static int Run(string[] args)
    {
        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        var mgr = new DependencyManager(m => Console.WriteLine("    " + m));

        return sub switch
        {
            "status" => Status(mgr),
            "install" => Install(mgr, args),
            "repair" => Install(mgr, args),
            "recommendation" => Recommendation(),
            "verify" => VerifyLocal(args),
            "access" => Access(),
            "configure" => Configure(),
            "paths" => Paths(),
            "resolve" => Resolve(mgr, args),
            // The two names the app and the tray menu already use, kept so a mixed-version
            // install does not break, now running the full setup rather than only the list.
            "repair-and-allow" => SetupAll(mgr, args, "Repair", repair: true),
            "install-and-allow" => SetupAll(mgr, args, "Setup", repair: false),
            "setup" => SetupAll(mgr, args, "Setup", repair: false),
            _ => Usage($"unknown 'deps' subcommand '{sub}'"),
        };
    }

    /// <summary>
    /// Shows which release each dependency would fetch, without fetching it. Read-only, and the
    /// quickest way to answer "am I about to get the current driver or the pinned fallback".
    /// </summary>
    private static int Resolve(DependencyManager mgr, string[] args)
    {
        bool pinned = args.Any(a => a.Equals("--pinned", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - WHAT AN INSTALL WOULD FETCH");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        foreach (var id in new[] { DependencyManager.ViGEmBusId, DependencyManager.HidHideId })
        {
            var r = mgr.ResolveLatestAsync(id, pinned).GetAwaiter().GetResult();
            Console.WriteLine($" {id}");
            Console.WriteLine($"   release : {r.Tag}{(r.Pinned ? "   (pinned fallback)" : "")}");
            Console.WriteLine($"   asset   : {r.FileName}");
            Console.WriteLine($"   url     : {r.Url}");
            Console.WriteLine($"   decided : {r.How}");
            Console.WriteLine();
        }
        Console.WriteLine(" Nothing was downloaded. The Authenticode check runs on the file itself,");
        Console.WriteLine(" at install time, and is what decides whether anything is executed.");
        return 0;
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine(problem);
        Console.Error.WriteLine("""
              deps status              what is installed and whether it works
              deps resolve [--pinned]  which release an install would fetch, without fetching
              deps install <id>        fetch, verify and silently install (id: vigembus | hidhide | all)
                                       skips anything already installed and working
              deps repair  <id>        same path, but reinstalls over a present-but-broken copy
              deps verify  <file>      Authenticode check on a local installer
              deps recommendation      latest-vs-pinned and bundle-vs-fetch, with the reasoning
              deps paths               where profiles are read and written, and why
              deps access              put this installation on HidHide's allow list only
              deps configure           allow list + hide the pad + cloak on (needs admin)
              deps setup [--pinned]    install both drivers and configure, one elevation prompt
              deps install-and-allow   alias for setup
              deps repair-and-allow    repair both drivers and configure, one elevation prompt

              --pinned forces the known-good build this release was tested against, instead of
              the current release from the vendor's own releases API.
            """);
        return 1;
    }

    /// <summary>
    /// Which profiles folder this build uses and how it got there.
    ///
    /// Worth a command of its own because the answer differs between a build tree and an
    /// install, and "my calibration disappeared" is otherwise indistinguishable from "the app
    /// is reading a different folder from the wizard that wrote it".
    /// </summary>
    private static int Paths()
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - PROFILE STORAGE");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        string dev = ProfileStore.DevelopmentTree() ?? "";
        string canonical = ProfileStore.CanonicalDirectory;
        string resolved = ProfileStore.Resolve();

        Console.WriteLine($" in use now        : {resolved}");
        Console.WriteLine($" per-user folder   : {(canonical.Length > 0 ? canonical : "(LocalApplicationData unavailable)")}");
        Console.WriteLine($" beside the exe    : {ProfileStore.BesideExecutable}" +
                          $"{(Directory.Exists(ProfileStore.BesideExecutable) ? "" : "   (not present)")}");
        Console.WriteLine($" build tree        : {(dev.Length > 0 ? dev : "(not running from one)")}");
        Console.WriteLine();

        bool migrated = canonical.Length > 0 &&
                        File.Exists(Path.Combine(canonical, ProfileStore.MigrationMarker));
        Console.WriteLine(dev.Length > 0
            ? " Running from a build tree, so the repository's own profiles folder is used and\n" +
              " nothing is copied into the user profile. An installed build ignores this path."
            : migrated
                ? " The one-time migration from the install folder has already run."
                : " The one-time migration has not run yet; it will on the next resolve.");
        Console.WriteLine();

        if (Directory.Exists(resolved))
        {
            var files = Directory.GetFiles(resolved, "*.json").Select(Path.GetFileName).ToList();
            Console.WriteLine($" {files.Count} file(s): {(files.Count == 0 ? "(empty)" : string.Join(", ", files))}");
        }
        else Console.WriteLine(" The folder does not exist yet; it is created on first write.");

        return 0;
    }

    /// <summary>
    /// Puts this installation on HidHide's allow list.
    ///
    /// HidHide hides the pad by executable path, so an installation that is not on the list has
    /// hidden the controller from itself and every open comes back as Win32 error 5, which looks
    /// exactly like an unplugged pad. This runs from the elevated helper because editing the
    /// list needs administrator rights, and it is the same helper the driver install already
    /// elevates into.
    /// </summary>
    private static int Access()
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - HIDHIDE DEVICE ACCESS");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        if (!HidHideAccess.Installed)
        {
            Console.WriteLine(" HidHide is not installed. Nothing hides the pad, so nothing needs allowing.");
            return 0;
        }

        Console.WriteLine($" cloaking      : {(HidHideAccess.Cloaking() ? "on" : "off")}");
        var hidden = HidHideAccess.HiddenDevices();
        Console.WriteLine($" hidden devices: {hidden.Count}");
        foreach (var d in hidden) Console.WriteLine($"                 {d}");

        var before = HidHideAccess.MissingRegistrations();
        Console.WriteLine($" not yet allowed: {before.Count}");
        foreach (var m in before) Console.WriteLine($"                 {m}");
        Console.WriteLine();

        if (before.Count == 0)
        {
            Console.WriteLine(" Every executable of this installation is already allowed through.");
            return 0;
        }

        if (!HidHideAccess.IsElevated())
        {
            Console.Error.WriteLine(" Editing the allow list needs administrator rights. Re-run elevated.");
            return 2;
        }

        bool ok = HidHideAccess.Register(m => Console.WriteLine("    " + m));
        Console.WriteLine();
        Console.WriteLine(ok ? " Done." : " Could not complete the allow list update.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The whole dependency step, in one elevated pass: install both drivers from their
    /// official sources, register this installation with HidHide, hide the pad, and cloak it.
    ///
    /// **One action, three outcomes, because a user has one complaint.** A missing driver, a
    /// pad hidden from us, and a pad not hidden from the game all produce "it does not work",
    /// and no user can tell which one they have. Setup and Repair both do all of it. Repair
    /// differs only in reinstalling over a driver that detection already calls present, which
    /// is the case somebody reaches for when the thing is there and still broken.
    /// </summary>
    private static int SetupAll(DependencyManager mgr, string[] args, string what, bool repair)
    {
        bool pinned = args.Any(a => a.Equals("--pinned", StringComparison.OrdinalIgnoreCase));
        int a = Install(mgr, ["deps", repair ? "repair" : "install", "all"], pinned, repair);
        Console.WriteLine();
        int b = Configure();
        bool padSeen = HidScan.Enumerate(DovetailEngine.DefaultVid, DovetailEngine.DefaultPid).Count > 0;
        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        // Do not claim the pad is configured when no pad was present to configure. The drivers
        // really are installed; saying more than that would hide the one step still outstanding.
        Console.WriteLine((a == 0 && b == 0, padSeen) switch
        {
            (true, true) => $" {what} finished. Both drivers are installed and the pad is configured.",
            (true, false) => $" {what} finished. Both drivers are installed. Plug the controller in and " +
                             "run this again to hide it from games.",
            _ => $" {what} finished with problems. The detail is above.",
        });
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();
        Console.WriteLine(" Press Enter to close.");
        Console.ReadLine();
        return a != 0 ? a : b;
    }

    /// <summary>
    /// Puts HidHide into the state Stage 4 proved a game needs: this installation allowed
    /// through, the physical pad hidden, cloaking on.
    ///
    /// Replaces the old "access" step, which only did the first of those three. The allow list
    /// alone stops HidHide hiding the pad from Dovetail; it does nothing about the actual
    /// problem HidHide is installed to solve, which is a game binding to the raw pad through
    /// DirectInput and ignoring the virtual one. See Findings Log 4.1.
    /// </summary>
    private static int Configure()
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - HIDHIDE DEVICE CONFIGURATION");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        if (!HidHideAccess.Installed)
        {
            Console.Error.WriteLine(" HidHide is not installed, so the pad cannot be hidden from games.");
            Console.Error.WriteLine(" Run 'dovetail-engine deps install hidhide' first.");
            return 3;
        }

        var pads = HidScan.Enumerate(DovetailEngine.DefaultVid, DovetailEngine.DefaultPid)
                          .Select(d => d.InstanceId)
                          .Where(s => s.Length > 0)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        Console.WriteLine($" cloaking       : {(HidHideAccess.Cloaking() ? "on" : "off")}");
        Console.WriteLine($" pad collections: {pads.Count}");
        foreach (var p in pads) Console.WriteLine($"                  {p}");
        Console.WriteLine();

        if (pads.Count == 0)
        {
            Console.WriteLine(" No controller is connected, so there is nothing to hide yet.");
            Console.WriteLine(" The allow list is still updated; re-run this with the pad plugged in.");
            Console.WriteLine();
            bool onlyList = HidHideAccess.Register(m => Console.WriteLine("    " + m));
            return onlyList ? 0 : 1;
        }

        if (!HidHideAccess.IsElevated())
        {
            Console.Error.WriteLine(" Configuring HidHide needs administrator rights. Re-run elevated.");
            return 2;
        }

        var applied = HidHideAccess.Configure(pads, m => Console.WriteLine("    " + m));

        // Record only what we changed, so the uninstaller can undo exactly that and leave a
        // setting the user or another application made alone.
        if (applied.DevicesHidden.Count > 0 || applied.CloakTurnedOn)
        {
            var state = HidHideState.Load();
            state.Merge(applied);
            if (state.Save()) Console.WriteLine($"    recorded our changes in {HidHideState.DefaultPath}");
            else Console.WriteLine("    WARNING: could not record what was changed, so uninstall cannot undo it");
        }

        Console.WriteLine();
        Console.WriteLine(applied.Success ? " Done." : " Could not complete the configuration.");
        return applied.Success ? 0 : 1;
    }

    private static int Status(DependencyManager mgr)
    {
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(" DOVETAIL - DEPENDENCY STATUS");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine();

        var all = mgr.DetectAll();
        foreach (var d in all)
        {
            string flag = d.State switch
            {
                DependencyState.Installed => "OK",
                DependencyState.Missing => d.Required ? "MISSING (required)" : "missing (optional)",
                DependencyState.InstalledButUnusable => "PRESENT BUT NOT WORKING",
                DependencyState.VersionTooOld => "TOO OLD",
                _ => "unknown",
            };
            Console.WriteLine($" {d.DisplayName}");
            Console.WriteLine($"   state          : {flag}");
            Console.WriteLine($"   driver version : {d.DriverVersion ?? "(none)"}");
            Console.WriteLine($"   package version: {d.PackageVersion ?? "(not in Add/Remove Programs)"}");
            Console.WriteLine($"   detail         : {d.Detail}");
            Console.WriteLine($"   needed because : {d.WhyItIsNeeded}");
            Console.WriteLine();
        }

        bool blocked = all.Any(d => d.Required && d.NeedsAction);
        Console.WriteLine(blocked
            ? " A required dependency needs attention. Run: dovetail-engine deps install vigembus"
            : " All required dependencies are present and working. The first-run screen would be skipped.");
        return blocked ? 3 : 0;
    }

    private static int Install(DependencyManager mgr, string[] args, bool pinned = false, bool repair = false)
    {
        string id = args.Length > 2 ? args[2].ToLowerInvariant() : "all";
        var ids = id == "all"
            ? new[] { DependencyManager.ViGEmBusId, DependencyManager.HidHideId }
            : [id];

        pinned = pinned || args.Any(a => a.Equals("--pinned", StringComparison.OrdinalIgnoreCase));
        repair = repair || (args.Length > 1 && args[1].Equals("repair", StringComparison.OrdinalIgnoreCase));

        int failures = 0;
        foreach (var one in ids)
        {
            Console.WriteLine(new string('-', 74));
            Console.WriteLine($" {(repair ? "REPAIRING" : "SETTING UP")}: {one}");
            Console.WriteLine(new string('-', 74));

            var progress = new Progress<double>(p =>
            {
                int pct = (int)(p * 100);
                if (pct % 10 == 0) Console.Write($"\r    downloading {pct,3}%");
            });

            // Detection lives inside InstallAsync so install and repair differ by one flag
            // rather than by two copies of the same check. Install skips what already works;
            // repair reinstalls over it, which is the point of repair.
            var result = repair
                ? mgr.RepairAsync(one, progress, default, pinned).GetAwaiter().GetResult()
                : mgr.InstallAsync(one, progress, default, pinned).GetAwaiter().GetResult();

            Console.WriteLine();
            if (result.AlreadyPresent)
            {
                Console.WriteLine($"   already present: {result.Message}");
                Console.WriteLine("   left exactly as it is; Dovetail does not assume it put it there");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"   step         : {result.Step}");
            Console.WriteLine($"   release      : {result.Source?.Tag ?? "(not resolved)"}" +
                              $"{(result.Source?.Pinned == true ? "   (pinned fallback)" : "")}");
            Console.WriteLine($"   source       : {result.Source?.How ?? ""}");
            Console.WriteLine($"   url          : {result.Source?.Url ?? ""}");
            Console.WriteLine($"   downloaded   : {result.DownloadedBytes} bytes");
            Console.WriteLine($"   sha256       : {result.Sha256}");
            Console.WriteLine($"   signed by    : {result.SignatureSubject ?? "(not signed)"}");
            Console.WriteLine($"   exit code    : {result.InstallerExitCode?.ToString() ?? "(not run)"}");
            Console.WriteLine($"   result       : {(result.Success ? "SUCCESS" : "FAILED")} - {result.Message}");
            Console.WriteLine();
            if (!result.Success) failures++;
        }
        return failures == 0 ? 0 : 3;
    }

    private static int VerifyLocal(string[] args)
    {
        string? path = args.Length > 2 ? args[2] : null;
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("usage: deps verify <path-to-installer>");
            return 1;
        }
        var (trusted, subject, why) = DependencyManager.VerifySignature(path);
        Console.WriteLine($" file    : {path}");
        Console.WriteLine($" sha256  : {DependencyManager.Sha256Of(path)}");
        Console.WriteLine($" trusted : {trusted}");
        Console.WriteLine($" subject : {subject ?? "(none)"}");
        Console.WriteLine($" verdict : {why}");
        return trusted ? 0 : 3;
    }

    private static int Recommendation()
    {
        Console.WriteLine(DependencyManager.DeliveryRecommendation);
        return 0;
    }
}
