namespace Dovetail.Core;

/// <summary>
/// Decides where calibration profiles, settings and the game library live, and moves them
/// there once for an installation that predates the decision.
///
/// **Why this exists.** Until the packaging step the profiles folder sat beside the
/// executable, found by walking up from the build output until a directory called
/// <c>profiles</c> appeared. That works from a build tree and is wrong for an installed
/// product in three separate ways. Program Files is not writable by a standard user, so the
/// first calibration fails with an access error on a machine where nothing is broken. A
/// per-machine folder is shared between Windows accounts, so two people on one PC overwrite
/// each other's calibrations. And an upgrade that replaces the install folder takes the
/// measured data with it, which is the one thing in this product that costs a full sweep per
/// pad to recreate.
///
/// <see cref="CanonicalDirectory"/> fixes all three: per-user, writable without elevation,
/// and untouched by an install or an uninstall unless the user asks for it to go.
///
/// **The three cases, in the order they are tried.**
///
/// 1. <b>A build tree.</b> The walk-up survives, and only for this. It is recognised by an
///    ancestor directory named <c>src</c> with a <c>profiles</c> folder beside it, which is
///    true of <c>src\Dovetail.App\bin\x64\Release\net8.0-windows</c> and of nothing else -
///    see <see cref="DevelopmentTree"/> for why the looser test was not good enough. A
///    development run keeps using the repository's measured profiles and copies nothing into
///    the user profile, because those files are this project's evidence.
///
/// 2. <b>An installation that has already migrated.</b> The marker file settles it. Testing
///    for the directory alone would be wrong: a crash log written before the first profile
///    is saved creates the directory, and migration would then be skipped for ever with
///    nothing in it.
///
/// 3. <b>First run.</b> The directory is created and seeded from <c>profiles</c> beside the
///    executable, which is what the installer stages. Nothing is overwritten and the source
///    is left alone, so a failed migration can simply be run again.
/// </summary>
public static class ProfileStore
{
    /// <summary>Folder under LocalApplicationData. Also the uninstaller's target.</summary>
    public const string ProductFolder = "Dovetail";

    /// <summary>
    /// Written into <see cref="CanonicalDirectory"/> once migration has been attempted.
    /// Its presence, not the directory's, is what makes migration run exactly once.
    /// </summary>
    public const string MigrationMarker = ".migrated";

    /// <summary>%LOCALAPPDATA%\Dovetail\profiles. Empty when Windows will not name the folder.</summary>
    public static string CanonicalDirectory
    {
        get
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return local.Length == 0 ? "" : Path.Combine(local, ProductFolder, "profiles");
            }
            catch { return ""; }
        }
    }

    /// <summary>Where an installer stages the seed copy: <c>profiles</c> beside the executable.</summary>
    public static string BesideExecutable => Path.Combine(AppContext.BaseDirectory, "profiles");

    /// <summary>
    /// The profiles folder for this run, migrating on first use. Every entry point calls this
    /// and no other resolver exists, because two resolvers that disagree would give the tray
    /// app and the calibration wizard different folders and lose a calibration that looked
    /// like it had saved.
    /// </summary>
    public static string Resolve(Action<string>? log = null)
    {
        log ??= _ => { };

        if (DevelopmentTree() is { } dev)
        {
            log($"profiles: development tree, using {dev}");
            return dev;
        }

        string canonical = CanonicalDirectory;
        if (canonical.Length == 0)
        {
            // No LocalApplicationData at all. Rare, but silently writing nowhere is worse
            // than falling back to the folder the executable can at least see.
            log("profiles: LocalApplicationData is unavailable, falling back beside the executable");
            return BesideExecutable;
        }

        if (File.Exists(Path.Combine(canonical, MigrationMarker))) return canonical;

        foreach (var line in Migrate(canonical)) log("profiles: " + line);
        return canonical;
    }

    /// <summary>
    /// Seeds <paramref name="canonical"/> from the install folder, once. Returns what it did,
    /// for the caller's log. Never throws: a tray app that cannot start because a copy failed
    /// is worse than one that starts with an empty profiles folder and says so.
    /// </summary>
    public static List<string> Migrate(string canonical)
    {
        var log = new List<string>();
        try
        {
            Directory.CreateDirectory(canonical);
        }
        catch (Exception ex)
        {
            log.Add($"could not create {canonical}: {ex.Message}");
            return log;
        }

        string source = BesideExecutable;
        // Path.GetFullPath normalises the trailing separator AppContext.BaseDirectory carries,
        // so the comparison below is not defeated by a formatting difference.
        bool sameFolder = string.Equals(
            Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(canonical).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        if (!sameFolder && Directory.Exists(source))
        {
            int copied = 0, kept = 0;
            foreach (var file in SafeFiles(source, log))
            {
                string dest = Path.Combine(canonical, Path.GetFileName(file));
                if (File.Exists(dest)) { kept++; continue; }
                try { File.Copy(file, dest); copied++; }
                catch (Exception ex) { log.Add($"could not copy {Path.GetFileName(file)}: {ex.Message}"); }
            }

            // _superseded holds retired calibrations. They are not read, but they are measured
            // data, and leaving them behind in a folder an uninstall deletes would lose them.
            string retired = Path.Combine(source, "_superseded");
            if (Directory.Exists(retired))
            {
                string destDir = Path.Combine(canonical, "_superseded");
                try { Directory.CreateDirectory(destDir); } catch { }
                foreach (var file in SafeFiles(retired, log))
                {
                    string dest = Path.Combine(destDir, Path.GetFileName(file));
                    if (File.Exists(dest)) { kept++; continue; }
                    try { File.Copy(file, dest); copied++; }
                    catch (Exception ex) { log.Add($"could not copy _superseded\\{Path.GetFileName(file)}: {ex.Message}"); }
                }
            }

            log.Add(copied == 0 && kept == 0
                ? $"nothing to migrate from {source}"
                : $"migrated {copied} file(s) from {source}" + (kept > 0 ? $", {kept} already present and left alone" : ""));
            // The source is deliberately left in place. It is the installer's staged copy, and
            // deleting it would make a re-install look like it had lost the calibration.
        }
        else
        {
            log.Add($"first run, no staged profiles to migrate; starting empty at {canonical}");
        }

        try
        {
            File.WriteAllText(Path.Combine(canonical, MigrationMarker),
                $"Migrated by Dovetail on {DateTime.Now:yyyy-MM-dd HH:mm:ss}." + Environment.NewLine +
                $"Source: {source}" + Environment.NewLine +
                "Delete this file to run the one-time migration from the install folder again." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Without the marker the migration repeats on the next launch. It is idempotent,
            // so that is untidy rather than harmful, but it is worth saying out loud.
            log.Add($"could not write the migration marker, this will run again next launch: {ex.Message}");
        }
        return log;
    }

    /// <summary>
    /// The repository's profiles folder when this executable is a build output inside that
    /// repository, and null in every other case.
    ///
    /// **The test is "am I under the repository's src directory", not "is there a profiles
    /// folder above me".** The looser test was wrong in a way that would only have shown up
    /// late: <c>dist\Dovetail</c> is staged inside the repository, so a plain walk-up finds
    /// the repository's profiles folder from there too, and the one build that exists to
    /// rehearse the installed layout would have quietly behaved like a development build.
    /// Requiring an ancestor literally named <c>src</c>, with a sibling <c>profiles</c>,
    /// matches <c>src\Dovetail.App\bin\x64\Release\net8.0-windows</c> and nothing else.
    /// A staged copy and an install both fall through to the per-user folder.
    /// </summary>
    public static string? DevelopmentTree()
    {
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (!dir.Name.Equals("src", StringComparison.OrdinalIgnoreCase)) continue;
                if (dir.Parent is null) continue;
                var candidate = Path.Combine(dir.Parent.FullName, "profiles");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        catch { /* an unreadable parent is simply not a development tree */ }
        return null;
    }

    private static IEnumerable<string> SafeFiles(string dir, List<string> log)
    {
        try { return Directory.GetFiles(dir); }
        catch (Exception ex) { log.Add($"could not list {dir}: {ex.Message}"); return []; }
    }
}
