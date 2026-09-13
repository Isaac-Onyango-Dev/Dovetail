namespace Dovetail.Core;

/// <summary>
/// All calibration profiles on disk, indexed so the engine can give each physical pad its
/// own numbers. Section 5.1 to 5.3 support N physical devices mapping to N virtual XInput
/// devices, and that means N calibrations, not one shared set.
///
/// **Profiles are keyed by receiver port, not by pad, and that is a hardware limit rather
/// than a design choice.** Measured on this setup: both pads report the same vendor id,
/// product id, revision 0x0200 and product string, and neither reports a serial number.
/// There is therefore nothing in the HID data that distinguishes one unit from the other.
/// The report id in byte 0 identifies which receiver port a report came from, so that is
/// what a profile can be tied to. The practical consequence is that re-pairing a pad to the
/// other channel leaves its calibration behind on the old channel. In normal use each pad
/// keeps its channel, so this is stable, but it is a limit worth knowing rather than
/// discovering.
///
/// A general set of unrelated controllers on different ports works through the same
/// mechanism, since each gets its own file keyed by the report id it produces.
/// </summary>
public sealed class ProfileSet
{
    private readonly Dictionary<byte, CalibrationProfile> _byReportId = [];
    private CalibrationProfile? _fallback;

    public string Directory { get; }
    public IReadOnlyDictionary<byte, CalibrationProfile> ByReportId => _byReportId;
    public CalibrationProfile? Fallback => _fallback;
    public List<string> LoadLog { get; } = [];

    private ProfileSet(string directory) => Directory = directory;

    /// <summary>Canonical file name for a port's profile.</summary>
    public static string FileNameForPort(byte reportId) => $"pad-port{reportId}.calibration.json";

    public static string PathForPort(string directory, byte reportId) =>
        Path.Combine(directory, FileNameForPort(reportId));

    /// <summary>
    /// Loads every *.calibration.json in the directory. A file whose device section carries
    /// a report id is indexed under it; default.calibration.json becomes the fallback for a
    /// port with no profile of its own.
    /// </summary>
    public static ProfileSet Load(string directory)
    {
        var set = new ProfileSet(directory);
        if (!System.IO.Directory.Exists(directory))
        {
            set.LoadLog.Add($"profile directory not found: {directory}");
            return set;
        }

        foreach (var file in System.IO.Directory.GetFiles(directory, "*.calibration.json")
                                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            CalibrationProfile p;
            try { p = CalibrationProfile.Load(file); }
            catch (Exception ex) { set.LoadLog.Add($"{Path.GetFileName(file)}: unreadable, {ex.Message}"); continue; }

            string name = Path.GetFileName(file);
            bool isDefault = name.StartsWith("default.", StringComparison.OrdinalIgnoreCase);

            if (p.Device.ReportId != 0 && !isDefault)
            {
                set._byReportId[p.Device.ReportId] = p;
                set.LoadLog.Add($"{name}: port {p.Device.ReportId} ({p.Device.CollectionTag})");
            }
            else if (isDefault)
            {
                set._fallback = p;
                set.LoadLog.Add($"{name}: fallback for any port without its own profile" +
                                (p.Device.ReportId != 0 ? $", measured on port {p.Device.ReportId}" : ""));
            }
        }

        if (set._byReportId.Count == 0 && set._fallback is null)
            set.LoadLog.Add("no usable profile found");
        return set;
    }

    /// <summary>
    /// Profile for a given report id: its own if it has one, otherwise the fallback.
    /// Returns null only when there is nothing at all to work with.
    /// </summary>
    public CalibrationProfile? For(byte reportId) =>
        _byReportId.TryGetValue(reportId, out var p) ? p : _fallback;

    /// <summary>True when this port has a calibration measured on that port specifically.</summary>
    public bool HasOwnProfile(byte reportId) => _byReportId.ContainsKey(reportId);

    /// <summary>Wraps one profile as a set, for callers that only have one.</summary>
    public static ProfileSet Single(CalibrationProfile profile)
    {
        var set = new ProfileSet(Path.GetDirectoryName(profile.Name) ?? "");
        if (profile.Device.ReportId != 0) set._byReportId[profile.Device.ReportId] = profile;
        set._fallback = profile;
        set.LoadLog.Add("single profile supplied directly by the caller");
        return set;
    }

    /// <summary>Any profile at all, for bootstrapping before a pad has been seen.</summary>
    public CalibrationProfile? Any() =>
        _fallback ?? _byReportId.Values.FirstOrDefault();
}
