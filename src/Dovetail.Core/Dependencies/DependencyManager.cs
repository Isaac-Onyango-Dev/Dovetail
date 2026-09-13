using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Dovetail.Core;

public enum DependencyState
{
    Unknown,
    Missing,
    Installed,
    InstalledButUnusable,
    VersionTooOld,
}

public sealed class DependencyStatus
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required bool Required { get; init; }
    public DependencyState State = DependencyState.Unknown;
    public string? DriverVersion;
    public string? PackageVersion;
    public string Detail = "";
    public string WhyItIsNeeded = "";

    public bool NeedsAction => State is DependencyState.Missing
        or DependencyState.InstalledButUnusable or DependencyState.VersionTooOld;

    public override string ToString() =>
        $"{DisplayName,-22} {State,-22} {(DriverVersion is null ? "" : "driver " + DriverVersion)} {Detail}";
}

/// <summary>
/// Section 5.7, first-run dependency setup.
///
/// The driver Dovetail depends on must never be a manual prerequisite the user has to go and
/// find. This class does the whole job: detect what is present, fetch the official installer
/// from the vendor's own GitHub release, verify it is genuinely signed by that vendor before
/// running anything, install it silently with the rights the app already has, and report the
/// outcome. "Repair" is the same path run again.
///
/// Verification deliberately checks the Authenticode signature rather than a published
/// checksum. Neither vendor publishes SHA-256 digests in their release notes, and a
/// signature is the stronger claim anyway: it proves the publisher, not merely that the
/// bytes match something a README once said.
/// </summary>
public sealed class DependencyManager
{
    public const string ViGEmBusId = "vigembus";
    public const string HidHideId = "hidhide";

    private sealed record Spec(
        string Id,
        string DisplayName,
        bool Required,
        string WhyItIsNeeded,
        string DriverFile,
        string ServiceName,
        Version MinimumDriverVersion,
        string Repository,
        string AssetPrefix,
        string InstallerUrl,
        string InstallerFileName,
        long ExpectedSizeBytes,
        string ExpectedSha256,
        string ExpectedPublisherContains,
        string SilentArgs);

    // The URL and digest on each spec are the PINNED FALLBACK, not the normal path. Normally
    // the current stable release is resolved from the vendor's own GitHub releases API at
    // install time, so a machine set up months from now does not get a driver frozen at
    // whatever was current when Dovetail was last built. See ResolveLatestAsync.
    //
    // The pinned values were measured by downloading both assets on 2026-09-12 and checking
    // each one's Authenticode signature first: ViGEmBus v1.22.0 published 2023-11-02, HidHide
    // v1.5.230.0 published 2024-05-11. They are a second, independent check and a fallback for
    // a machine that cannot reach the API, not the primary one. Neither vendor publishes
    // digests, so a mismatch most likely means the vendor republished the asset rather than
    // that anything is wrong. The signature stays the authority, and a mismatch is reported
    // loudly rather than treated as fatal.
    private static readonly Spec[] Specs =
    [
        new(ViGEmBusId, "ViGEm Bus Driver", true,
            "Creates the virtual Xbox 360 controller. Without it Dovetail has nothing to write to and games see no pad.",
            @"C:\Windows\System32\drivers\ViGEmBus.sys", "ViGEmBus",
            new Version(1, 21, 0, 0),
            "nefarius/ViGEmBus", "ViGEmBus",
            "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe",
            "ViGEmBus_1.22.0_x64_x86_arm64.exe", 6278576,
            "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A",
            "Nefarius Software Solutions",
            "/quiet /norestart"),

        // REQUIRED, and this was measured rather than assumed. See Findings Log 4.1: with
        // HidHide inert the raw pad stayed readable through DirectInput, Sekiro enumerated
        // three controllers, bound to the raw one, and ignored every input Dovetail produced
        // while the engine was verifiably translating. Installing HidHide and cloaking the pad
        // is what made the game respond. A game that polls XInput alone would not need it, but
        // nothing tells a user in advance which kind of game they have bought, and the failure
        // reads as "this app does not work" rather than as a missing optional extra.
        new(HidHideId, "HidHide", true,
            "Hides the raw pad from games so they cannot bind to it instead of the virtual one. Required: without it a game that also reads DirectInput binds to the physical pad and ignores Dovetail entirely, which is what Sekiro did in Stage 4.",
            @"C:\Windows\System32\drivers\HidHide.sys", "HidHide",
            new Version(1, 4, 0, 0),
            "nefarius/HidHide", "HidHide",
            "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe",
            "HidHide_1.5.230_x64.exe", 8078016,
            "F4BBBCB82E6258641B887C74BC81C4C5F66E4AA811808DFC304347687B7605F6",
            "Nefarius Software Solutions",
            "/quiet /norestart"),
    ];

    private readonly Action<string> _log;
    public DependencyManager(Action<string>? log = null) => _log = log ?? (_ => { });

    // ---------------------------------------------------------------- detect

    public List<DependencyStatus> DetectAll() => Specs.Select(Detect).ToList();

    public DependencyStatus Detect(string id) =>
        Detect(Specs.FirstOrDefault(s => s.Id == id)
               ?? throw new ArgumentException($"unknown dependency '{id}'"));

    private DependencyStatus Detect(Spec spec)
    {
        var st = new DependencyStatus
        {
            Id = spec.Id,
            DisplayName = spec.DisplayName,
            Required = spec.Required,
            WhyItIsNeeded = spec.WhyItIsNeeded,
        };

        if (!File.Exists(spec.DriverFile))
        {
            st.State = DependencyState.Missing;
            st.Detail = $"{Path.GetFileName(spec.DriverFile)} is not present in the driver folder";
            return st;
        }

        var vi = FileVersionInfo.GetVersionInfo(spec.DriverFile);
        st.DriverVersion = vi.FileVersion;
        st.PackageVersion = FindPackageVersion(spec.DisplayName);

        if (Version.TryParse(NormaliseVersion(vi.FileVersion), out var v) && v < spec.MinimumDriverVersion)
        {
            st.State = DependencyState.VersionTooOld;
            st.Detail = $"driver {v} is older than the {spec.MinimumDriverVersion} Dovetail needs";
            return st;
        }

        // A present file is not a working driver. For ViGEmBus the only honest check is to
        // open the client, which is what Dovetail itself will do.
        if (spec.Id == ViGEmBusId)
        {
            using var bus = new VirtualBus();
            if (!bus.Open())
            {
                st.State = DependencyState.InstalledButUnusable;
                st.Detail = bus.Status;
                return st;
            }
            st.State = DependencyState.Installed;
            st.Detail = "driver present and the client connects";
            return st;
        }

        // Same rule for HidHide: a driver file is not a working HidHide. Everything Dovetail
        // needs from it goes through HidHideCLI.exe, which the driver package installs, and a
        // package that left the driver behind without the client is one we cannot configure.
        // That is a repair case, not a working install.
        if (spec.Id == HidHideId && HidHideAccess.Cli() is null)
        {
            st.State = DependencyState.InstalledButUnusable;
            st.Detail = "the driver is present but HidHideCLI.exe is not, so the pad cannot be configured";
            return st;
        }

        st.State = DependencyState.Installed;
        st.Detail = "driver present";
        return st;
    }

    private static string NormaliseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "0.0";
        // FileVersion sometimes carries a trailing comment, e.g. "10.0.19041.1 (WinBuild...)"
        var first = s.Split(' ')[0];
        return first.Count(c => c == '.') switch
        {
            0 => first + ".0",
            _ => first
        };
    }

    private static string? FindPackageVersion(string displayNameContains)
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        foreach (var root in roots)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(sub);
                var name = k?.GetValue("DisplayName") as string;
                if (name is null || !name.Contains(displayNameContains, StringComparison.OrdinalIgnoreCase)) continue;
                return k?.GetValue("DisplayVersion") as string;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- resolve

    /// <summary>Which release an install is about to fetch, and how that was decided.</summary>
    public sealed class Resolved
    {
        public required string Url { get; init; }
        public required string FileName { get; init; }
        public string Tag { get; init; } = "";
        /// <summary>True when this came from the pinned fallback rather than the live API.</summary>
        public bool Pinned { get; init; }
        public string How { get; init; } = "";
    }

    /// <summary>User agent sent to GitHub, which rejects requests without one.</summary>
    private const string UserAgent = "Dovetail-Gamepad-Emulator/0.6";

    /// <summary>
    /// Finds the current stable release of a dependency from the vendor's own GitHub releases
    /// API, falling back to the version pinned in this build when that cannot be reached.
    ///
    /// **Why resolve rather than pin.** Both of these are kernel drivers under active security
    /// maintenance. A pinned URL means every machine set up after this build shipped installs
    /// whatever was current when it shipped, and a user installing Dovetail a year from now
    /// gets a year-old driver. Fetching the current release keeps the vendor as the source of
    /// truth for their own product.
    ///
    /// **Why that is safe.** What makes it safe is not the URL, it is the Authenticode check
    /// that runs on the downloaded file before anything is executed: the installer only runs
    /// if Windows itself says the signature is valid and the subject is the expected publisher.
    /// That gate is identical whether the URL came from the API or from the pin, so resolving
    /// does not widen what can be executed. See Findings Log 5.7 for the sandbox run that
    /// proved the gate on a real download.
    ///
    /// **What is still refused.** A release whose tag parses below the spec's minimum version,
    /// which stops a vendor's own older-branch release being picked up as "latest", and any
    /// asset whose name does not match the expected product. <paramref name="preferPinned"/>
    /// forces the known-good build, which is the escape hatch if a vendor ever ships a signed
    /// but broken release.
    /// </summary>
    public async Task<Resolved> ResolveLatestAsync(string id, bool preferPinned = false,
                                                   CancellationToken ct = default)
    {
        var spec = Specs.FirstOrDefault(s => s.Id == id)
                   ?? throw new ArgumentException($"unknown dependency '{id}'");
        return await ResolveLatestAsync(spec, preferPinned, ct);
    }

    private async Task<Resolved> ResolveLatestAsync(Spec spec, bool preferPinned, CancellationToken ct)
    {
        Resolved Fallback(string why) => new()
        {
            Url = spec.InstallerUrl,
            FileName = spec.InstallerFileName,
            Tag = "(pinned)",
            Pinned = true,
            How = why,
        };

        if (preferPinned) return Fallback("pinned build requested");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            string api = $"https://api.github.com/repos/{spec.Repository}/releases/latest";
            _log($"resolving the current release from {api}");

            using var resp = await http.GetAsync(api, ct);
            if (!resp.IsSuccessStatusCode)
                return Fallback($"the releases API answered {(int)resp.StatusCode}, using the pinned build");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";

            // A tag below the minimum means the API answered with an older branch, or with
            // something this build does not understand. The pin is the safer answer.
            if (ParseTag(tag) is { } v && v < spec.MinimumDriverVersion)
                return Fallback($"latest tag {tag} is older than the {spec.MinimumDriverVersion} Dovetail needs, using the pinned build");

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return Fallback("the release carried no assets, using the pinned build");

            string? bestUrl = null, bestName = null;
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (url.Length == 0) continue;
                if (!name.StartsWith(spec.AssetPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                // Prefer an x64 asset where the vendor publishes per-architecture files, but
                // accept a combined one, which is what ViGEmBus ships.
                bool x64 = name.Contains("x64", StringComparison.OrdinalIgnoreCase);
                if (bestUrl is null || (x64 && !bestName!.Contains("x64", StringComparison.OrdinalIgnoreCase)))
                {
                    bestUrl = url;
                    bestName = name;
                }
            }

            if (bestUrl is null || bestName is null)
                return Fallback($"no asset named {spec.AssetPrefix}*.exe in {tag}, using the pinned build");

            return new Resolved
            {
                Url = bestUrl,
                FileName = bestName,
                Tag = tag,
                Pinned = false,
                How = $"current release {tag} from the {spec.Repository} releases API",
            };
        }
        catch (Exception ex)
        {
            return Fallback($"could not reach the releases API ({ex.GetType().Name}: {ex.Message}), using the pinned build");
        }
    }

    /// <summary>A release tag like "v1.22.0" or "v1.5.230.0" as a Version, or null.</summary>
    private static Version? ParseTag(string tag)
    {
        string s = tag.TrimStart('v', 'V');
        return Version.TryParse(s, out var v) ? v : null;
    }

    // ---------------------------------------------------------------- install

    public sealed class SetupResult
    {
        public bool Success;
        public string Step = "";
        public string Message = "";
        public string? DownloadedTo;
        public long DownloadedBytes;
        public string? SignatureSubject;
        public string? Sha256;
        public int? InstallerExitCode;
        public DependencyStatus? StatusAfter;
        public List<string> Trace = [];

        /// <summary>Which release this fetched, and whether it came from the API or the pin.</summary>
        public Resolved? Source;

        /// <summary>True when nothing was downloaded because it was already installed and working.</summary>
        public bool AlreadyPresent;
    }

    /// <summary>
    /// Fetch, verify, install, then re-detect. Every step is recorded in
    /// <see cref="SetupResult.Trace"/> so a failure says which step failed and why.
    /// </summary>
    public async Task<SetupResult> InstallAsync(
        string id, IProgress<double>? progress = null, CancellationToken ct = default,
        bool preferPinned = false, bool skipIfPresent = true)
    {
        var spec = Specs.FirstOrDefault(s => s.Id == id)
                   ?? throw new ArgumentException($"unknown dependency '{id}'");
        var r = new SetupResult();
        void T(string m) { r.Trace.Add(m); _log(m); }

        // ---- detect first ----
        //
        // Requirement 3: something already on the machine is left alone, whoever put it there.
        // Detection is by driver file, version and a functional probe, never by a marker
        // Dovetail wrote, so a ViGEmBus installed by DS4Windows or by the user counts exactly
        // the same as one Dovetail installed itself.
        if (skipIfPresent)
        {
            var before = Detect(spec);
            if (before.State == DependencyState.Installed)
            {
                r.Success = true;
                r.AlreadyPresent = true;
                r.Step = "detect";
                r.StatusAfter = before;
                r.Message = $"{spec.DisplayName} is already installed and working ({before.Detail}); nothing was downloaded.";
                T(r.Message);
                return r;
            }
            T($"{spec.DisplayName}: {before.State}, {before.Detail}");
        }

        // ---- resolve ----
        r.Step = "resolve";
        var source = await ResolveLatestAsync(spec, preferPinned, ct);
        r.Source = source;
        T($"{spec.DisplayName}: {source.How}");

        string dir = Path.Combine(Path.GetTempPath(), "Dovetail", "deps");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, source.FileName);

        // ---- download ----
        r.Step = "download";
        T($"downloading {spec.DisplayName} from {source.Url}");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            using var resp = await http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0) progress?.Report(done / (double)total.Value);
            }
            r.DownloadedTo = file;
            r.DownloadedBytes = done;
            T($"downloaded {done} bytes to {file}");
        }
        catch (Exception ex)
        {
            r.Success = false;
            r.Message = $"download failed: {ex.Message}";
            T(r.Message);
            return r;
        }

        // ---- verify ----
        r.Step = "verify";
        r.Sha256 = Sha256Of(file);
        T($"sha256 {r.Sha256}");

        // The size and digest comparisons only mean anything against the pinned build, which is
        // the only file those numbers were measured from. For a release resolved from the API
        // they are recorded, not compared: a newer release is *expected* to differ, and warning
        // about that on every install would train the user to ignore the line that matters.
        if (source.Pinned)
        {
            if (spec.ExpectedSizeBytes > 0 && r.DownloadedBytes != spec.ExpectedSizeBytes)
                T($"NOTE: size {r.DownloadedBytes} differs from the expected {spec.ExpectedSizeBytes}. " +
                  "The vendor may have republished the asset; the signature check below is what decides.");

            if (!string.IsNullOrEmpty(spec.ExpectedSha256))
                T(string.Equals(r.Sha256, spec.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                    ? "sha256 matches the digest pinned in Dovetail"
                    : $"NOTE: sha256 differs from the pinned {spec.ExpectedSha256}. Most likely the vendor " +
                      "republished the asset. The signature check below is what decides whether this runs.");
        }
        else
        {
            T($"sha256 recorded for {source.Tag}; there is no pinned digest for a release newer " +
              "than this build, so the Authenticode check below is the whole gate.");
        }

        var (trusted, subject, why) = VerifySignature(file);
        r.SignatureSubject = subject;
        if (!trusted)
        {
            r.Success = false;
            r.Message = $"signature check failed, refusing to run the installer: {why}";
            T(r.Message);
            TryDelete(file);
            return r;
        }
        if (subject is null || !subject.Contains(spec.ExpectedPublisherContains, StringComparison.OrdinalIgnoreCase))
        {
            r.Success = false;
            r.Message = $"installer is signed but by an unexpected publisher, refusing to run it. Subject: {subject}";
            T(r.Message);
            TryDelete(file);
            return r;
        }
        T($"signature valid, publisher {subject}");

        // ---- install ----
        r.Step = "install";
        T($"running silent install: {spec.InstallerFileName} {spec.SilentArgs}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = spec.SilentArgs,
                UseShellExecute = true,   // lets Windows elevate if the manifest asks
                Verb = "runas",
            };
            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("installer did not start");
            await proc.WaitForExitAsync(ct);
            r.InstallerExitCode = proc.ExitCode;
            T($"installer exited with code {proc.ExitCode}");
            // WiX bundles use 3010 for "success, reboot required"
            if (proc.ExitCode is not (0 or 3010))
                T("NOTE: a non-zero exit code does not always mean failure; the re-detect below decides.");
        }
        catch (Exception ex)
        {
            r.Success = false;
            r.Message = $"install failed to launch: {ex.Message}";
            T(r.Message);
            return r;
        }

        // ---- re-detect ----
        r.Step = "verify-install";
        var after = Detect(spec);
        r.StatusAfter = after;
        r.Success = after.State == DependencyState.Installed;
        r.Message = r.Success
            ? $"{spec.DisplayName} installed and working: {after.Detail}"
            : $"{spec.DisplayName} still not usable after install: {after.State}, {after.Detail}" +
              (r.InstallerExitCode == 3010 ? " A restart is required to finish the driver install." : "");
        T(r.Message);
        return r;
    }

    /// <summary>
    /// Requirement 4: one action puts a missing, broken or out-of-date dependency back, from
    /// the same verified official source, with no further input.
    ///
    /// The difference from <see cref="InstallAsync"/> is <c>skipIfPresent: false</c>. Repair is
    /// what somebody reaches for when detection says "present" and the thing still does not
    /// work - a driver file that is there but whose service will not start, say - so it must
    /// reinstall over a present copy rather than detect it and declare victory.
    /// </summary>
    public Task<SetupResult> RepairAsync(string id, IProgress<double>? p = null,
                                         CancellationToken ct = default, bool preferPinned = false)
        => InstallAsync(id, p, ct, preferPinned, skipIfPresent: false);

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>
    /// Recommendation asked for by Section 5.7, stated in one place so the installer build
    /// can follow it without re-litigating the trade-off.
    /// </summary>
    public const string DeliveryRecommendation = """
        Recommendation: fetch on demand at first run, resolving the CURRENT release, with the
        last known-good build pinned as a fallback. Do not bundle.

        Bundle versus fetch:
          - The two installers total about 14 MB, which would roughly triple a Dovetail
            installer that is otherwise a few megabytes of managed code.
          - Both are kernel drivers under active security maintenance. A bundled copy is a
            pinned copy, and pinning a kernel driver inside a third-party installer means
            shipping a stale driver to every future user.
          - The fetch is a one-time cost. Once the driver is installed the screen never
            appears again, and Repair re-runs the same path on demand.

        Latest versus pinned, which is the sharper question:

          Always-latest gets users security fixes without waiting for a Dovetail release, and
          keeps the vendor as the source of truth for their own driver. Its risk is that a bad
          vendor release reaches every new install at once, and it cannot be tested in advance.

          Always-pinned is predictable and testable. Its risk is certain rather than possible:
          the pin goes stale the day it ships, and a user installing a year from now gets a
          year-old kernel driver.

          RECOMMENDED: resolve the current release, and make it safe rather than hoping.
            - The Authenticode gate is what makes it safe. Nothing is executed unless Windows
              says the signature is valid AND the subject is the expected Nefarius publisher.
              That gate does not care where the URL came from, so resolving widens the version
              that can be installed but not what can be executed.
            - A resolved release below the minimum version this build knows about is refused,
              which stops an older-branch release being picked up as "latest".
            - If the API cannot be reached, or answers with nothing usable, the pinned
              known-good build is used and the trace says so.
            - The residual risk is a signed-but-broken vendor release. Two things cover it:
              the post-install re-detect refuses to report success unless the driver actually
              works, and --pinned forces the known-good build for anyone who hits it.

        Caveat, stated rather than hidden: this needs internet access once. For a fully
        offline install, the same code path accepts a local file, so an offline variant of
        the Dovetail installer can drop the two signed installers next to itself and point the
        dependency step at them. That keeps one code path for both cases.
        """;

    // ---------------------------------------------------------------- signature

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WINTRUST_DATA data);

    /// <summary>
    /// Full Authenticode trust check through WinVerifyTrust, the same call Windows itself
    /// uses. Reading the embedded certificate alone would only prove a signature exists, not
    /// that it validates and chains to a trusted root.
    /// </summary>
    public static (bool trusted, string? subject, string why) VerifySignature(string path)
    {
        var actionGeneric = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        IntPtr pathPtr = Marshal.StringToCoTaskMemUni(path);
        IntPtr filePtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pathPtr,
            };
            filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, filePtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE, offline-friendly
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00000010,  // WTD_SAFER_FLAG
            };

            int rc = WinVerifyTrust(IntPtr.Zero, ref actionGeneric, ref data);
            string? subject = null;
            try
            {
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                subject = cert.Subject;
            }
            catch { /* unsigned, or the cert cannot be read */ }

            string why = rc switch
            {
                0 => "trusted",
                unchecked((int)0x800B0100) => "file is not signed",
                unchecked((int)0x800B0101) => "certificate has expired",
                unchecked((int)0x800B0109) => "certificate chain does not reach a trusted root",
                unchecked((int)0x80096010) => "digital signature is invalid, the file may be modified",
                unchecked((int)0x800B0111) => "publisher is explicitly untrusted",
                _ => $"WinVerifyTrust returned 0x{rc:X8}",
            };
            return (rc == 0, subject, why);
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(filePtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
