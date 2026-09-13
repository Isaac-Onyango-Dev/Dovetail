namespace Dovetail.Core;

/// <summary>Status of one physical pad the engine is tracking.</summary>
public sealed class PadChannel
{
    public required HidCollectionInfo Info { get; init; }
    public required HidReader Reader { get; init; }
    public byte ReportId;
    public byte[] RestState = [];
    /// <summary>Calibration for the player slot this pad claimed, or null if it claimed none.</summary>
    public CalibrationProfile? Profile;
    public HidDecoder? Decoder;
    public XInputMapper? Mapper;
    /// <summary>Player slot claimed, 1 to 4. Zero means unrecognised, so no virtual device.</summary>
    public int Slot;
    /// <summary>
    /// Set once the "new controller, not set up" notice has been raised for this connection.
    /// Section 7.2 forbids nagging: the notice is raised once per connect, never on a timer
    /// while the device sits there connected and idle.
    /// </summary>
    public bool UnrecognisedAnnounced;
    public string DisplayName => Profile?.EffectiveName ?? "Unrecognised controller";
    public bool Bound;                 // has a virtual pad been created for it
    public VirtualPad? Virtual;
    public PadState? Last;
    public XInputReport LastReport;
    public long ReportsRead;
    public long IdleReports;
    public DateTime LastActivityUtc = DateTime.MinValue;
    public DateTime LastReportUtc = DateTime.MinValue;

    public string Tag => Info.CollectionTag.Length > 0 ? Info.CollectionTag : $"rid{ReportId}";
    public bool Live => LastReportUtc != DateTime.MinValue &&
                        (DateTime.UtcNow - LastReportUtc).TotalSeconds < 2;
}

/// <summary>
/// The translation engine. Reads raw HID reports from every attached pad, decodes them with
/// the Stage 1 map, scales them with the Stage 2 calibration, and drives one virtual Xbox
/// 360 controller per pad.
///
/// Two rules come from the findings and are deliberately not negotiable here:
///
/// 1. **Never bind by enumeration index or by a remembered report id.** This adapter exposes
///    two joystick collections and streams idle reports on both whether or not a pad is
///    attached to that port, and the operator has two identical pads that can occupy either
///    port. Binding is therefore lazy: a collection gets a virtual pad the first time it
///    produces input, which also means the pad the user actually picks up becomes player 1.
///    Findings Log 1.8 and 2.6.
///
/// 2. **A bound pad keeps its virtual device even while idle.** A resting pad looks exactly
///    like an absent one on this hardware, so unbinding on silence would drop the virtual
///    device mid-game. Unbinding happens on device removal only.
/// </summary>
public sealed class DovetailEngine : IDisposable
{
    private SlotRegistry _slots;
    private readonly VirtualBus _bus = new();
    private readonly List<PadChannel> _channels = [];
    private readonly ushort _vid, _pid;
    private readonly Action<string> _log;
    private bool _disposed;

    public IReadOnlyList<PadChannel> Channels => _channels;
    public VirtualBus Bus => _bus;
    public SlotRegistry Slots => _slots;

    /// <summary>
    /// Fires for every report the engine decodes, at full report rate, before any display
    /// throttling. Exists so loss and latency can be measured on the real path rather than
    /// inferred from a console view that only samples a few times a second.
    /// The bool is true when this report resulted in a submission to the virtual pad.
    /// </summary>
    public event Action<PadChannel, PadState, XInputReport, bool>? StateDecoded;

    /// <summary>A controller claimed a calibrated slot and now has a virtual device.</summary>
    public event Action<PadChannel>? ControllerConnected;

    /// <summary>A controller's device disappeared and its virtual device was released.</summary>
    public event Action<PadChannel>? ControllerDisconnected;

    /// <summary>
    /// A controller produced input but had no calibrated slot to claim, so it got no virtual
    /// device. Raised once per connection. Section 7.2.
    /// </summary>
    public event Action<PadChannel>? UnrecognisedController;

    /// <summary>
    /// The adapter this project was built against, identified in Stage 1. Named here rather
    /// than repeated as a literal, because the diagnostics, the engine and the calibration
    /// screen all have to agree on which device they are talking about.
    /// </summary>
    public const ushort DefaultVid = 0x0810;
    public const ushort DefaultPid = 0x0001;

    public DovetailEngine(SlotRegistry slots, Action<string>? log = null)
        : this(slots, DefaultVid, DefaultPid, log) { }

    public DovetailEngine(SlotRegistry slots, ushort vid, ushort pid, Action<string>? log = null)
    {
        _slots = slots;
        _log = log ?? (_ => { });
        var seed = slots.Slots.Values.FirstOrDefault();
        _vid = seed is not null ? ParseHex(seed.Device.Vid, vid) : vid;
        _pid = seed is not null ? ParseHex(seed.Device.Pid, pid) : pid;
    }

    private static ushort ParseHex(string s, ushort fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : fallback;
    }

    /// <summary>Opens the virtual bus. False means no virtual pad can be created; see Bus.Status.</summary>
    public bool Start()
    {
        bool ok = _bus.Open();
        _log($"virtual bus: {_bus.Status}");
        return ok;
    }

    /// <summary>
    /// Swaps in a new calibration without dropping the virtual pads or the open readers.
    ///
    /// This exists so stick feel can be tuned while a game is running. Deadzone,
    /// anti-deadzone and response curve are the settings most likely to need a few attempts
    /// to get right, and needing to quit the game between attempts makes that unusable.
    /// The virtual controller stays connected across a reload, so the game never sees the
    /// pad disappear.
    /// </summary>
    public void ReloadProfiles(SlotRegistry slots)
    {
        _slots = slots;

        // Re-resolve every bound channel against the new set, so a change to one port's
        // profile does not disturb the other.
        // Re-resolve every claimed slot against the new registry. A slot that has been
        // forgotten loses its virtual device, per Section 7.4.
        foreach (var ch in _channels.Where(c => c.Slot > 0).ToList())
        {
            var p = _slots.ForSlot(ch.Slot);
            if (p is null)
            {
                _log($"{ch.Tag}: slot {ch.Slot} was forgotten, releasing its virtual device");
                ReleaseSlot(ch);
                continue;
            }
            AttachSlot(ch, ch.Slot, p);
            ch.Virtual?.InvalidateLastReport();
            var a = p.Axes.GetValueOrDefault("leftStickX");
            _log($"\"{p.EffectiveName}\" (slot {ch.Slot}) calibration reloaded: deadzone {a?.Deadzone}, " +
                 $"anti-deadzone {a?.AntiDeadzone}, curve {a?.ResponseCurve:0.##}, saturation {a?.Saturation}");
        }
    }

    private void AttachSlot(PadChannel ch, int slot, CalibrationProfile p)
    {
        ch.Slot = slot;
        ch.Profile = p;
        ch.Decoder = new HidDecoder(p);
        ch.Mapper = new XInputMapper(p);
    }

    private void ReleaseSlot(PadChannel ch)
    {
        ch.Virtual?.Dispose();
        ch.Virtual = null;
        ch.Bound = false;
        ch.Slot = 0;
        ch.Profile = null;
        ch.Decoder = null;
        ch.Mapper = null;
        ch.UnrecognisedAnnounced = false;
    }

    /// <summary>
    /// Rescans for physical collections and opens a reader for any that is new. Safe to call
    /// repeatedly; existing channels and their virtual pads are left alone.
    /// </summary>
    public void Rescan()
    {
        var present = HidScan.Enumerate(_vid, _pid);
        var presentPaths = present.Select(p => p.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // drop channels whose device is gone, so no stale virtual pad is left behind
        for (int i = _channels.Count - 1; i >= 0; i--)
        {
            var ch = _channels[i];
            if (presentPaths.Contains(ch.Info.DevicePath)) continue;
            if (ch.Slot > 0)
            {
                _log($"\"{ch.DisplayName}\" (slot {ch.Slot}) disconnected, releasing its virtual device");
                ControllerDisconnected?.Invoke(ch);
            }
            else
            {
                _log($"{ch.Tag}: device removed");
            }
            ch.Virtual?.Dispose();
            ch.Reader.Dispose();
            _channels.RemoveAt(i);
        }

        foreach (var info in present.OrderBy(p => p.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            if (_channels.Any(c => string.Equals(c.Info.DevicePath, info.DevicePath, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var reader = new HidReader(info.DevicePath, info.Caps.InputReportByteLength);
                _channels.Add(new PadChannel { Info = info, Reader = reader });
                _log($"{(info.CollectionTag.Length > 0 ? info.CollectionTag : info.DevicePath)}: reader opened, " +
                     $"{info.Caps.InputReportByteLength}-byte reports");
            }
            catch (Exception ex)
            {
                _log($"{info.CollectionTag}: cannot open, {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Polls every channel once and pushes any changed state to its virtual pad. Call this
    /// in a tight loop. Returns the number of reports submitted to virtual devices.
    /// </summary>
    public int Pump(int perChannelTimeoutMs = 4)
    {
        int submitted = 0;

        foreach (var ch in _channels)
        {
            byte[]? r;
            try { r = ch.Reader.Read(perChannelTimeoutMs); }
            catch (Exception ex) { _log($"{ch.Tag}: read error, {ex.Message}"); continue; }
            if (r is null || r.Length == 0) continue;

            if (ch.ReportId == 0) ch.ReportId = r[0];
            ch.ReportsRead++;
            ch.LastReportUtc = DateTime.UtcNow;
            if (ch.RestState.Length == 0) ch.RestState = r;

            // A pad needs a calibrated slot before anything can be decoded, because the
            // decoder is built from that slot's byte and bit map. Until it claims one it is
            // read but not translated.
            if (ch.Slot == 0)
            {
                if (!LooksActive(r, ch.RestState)) continue;

                int? claim = _slots.ClaimNextSlot(_channels.Where(c => c.Slot > 0).Select(c => c.Slot));
                if (claim is null)
                {
                    // Section 7.2: announce once per connection, never on a timer, and give
                    // it no virtual device.
                    if (!ch.UnrecognisedAnnounced)
                    {
                        ch.UnrecognisedAnnounced = true;
                        _log($"{ch.Tag}: a controller produced input but no calibrated slot is free. " +
                             "No virtual device created. Calibrate it to use it.");
                        UnrecognisedController?.Invoke(ch);
                    }
                    continue;
                }

                var prof = _slots.ForSlot(claim.Value)!;
                AttachSlot(ch, claim.Value, prof);
                _log($"{ch.Tag}: claimed slot {claim.Value} as \"{prof.EffectiveName}\"");
            }

            if (ch.Decoder is null || ch.Mapper is null || !ch.Decoder.CanDecode(r)) continue;

            var state = ch.Decoder.Decode(r);
            ch.Last = state;

            bool moved = state.AnyInputActive || AxesMoved(state, ch.Profile!);
            if (moved) ch.LastActivityUtc = DateTime.UtcNow;
            else ch.IdleReports++;

            // The slot is claimed, so a virtual device is created on first real activity.
            if (!ch.Bound && moved)
            {
                if (!_bus.Available)
                {
                    _log($"{ch.Tag}: input seen but the virtual bus is unavailable ({_bus.Status})");
                }
                else
                {
                    ch.Virtual = _bus.CreatePad();
                    ch.Virtual.Connect();
                    ch.Bound = true;
                    string xi = ch.Virtual.UserIndex is int u ? $"XInput slot {u}" : "XInput slot pending";
                    _log($"\"{ch.DisplayName}\" (slot {ch.Slot}) connected, {xi}");
                    ControllerConnected?.Invoke(ch);
                }
            }

            var report = ch.Mapper.Map(state);
            ch.LastReport = report;

            bool sent = false;
            if (ch.Virtual is not null && ch.Virtual.Submit(report))
            {
                submitted++;
                sent = true;
            }

            StateDecoded?.Invoke(ch, state, report, sent);
        }

        return submitted;
    }

    /// <summary>
    /// Whether a raw report shows real activity, judged without a calibration profile.
    /// Needed because a pad has to prove it is being used before it can claim a slot, and at
    /// that point there is no decoder for it yet.
    /// </summary>
    private static bool LooksActive(byte[] report, byte[] rest)
    {
        if (rest.Length == 0 || report.Length != rest.Length) return false;
        for (int i = 1; i < report.Length; i++)          // skip byte 0, the report id
            if (Math.Abs(report[i] - rest[i]) > 20) return true;
        return false;
    }

    private static bool AxesMoved(PadState s, CalibrationProfile profile)
    {
        bool Off(string name, int raw, int fallbackCentre)
        {
            var a = profile.Axes.GetValueOrDefault(name);
            int centre = a?.Centre ?? fallbackCentre;
            int dz = Math.Max(1, a?.Deadzone ?? 8);
            return Math.Abs(raw - centre) > dz;
        }
        return Off("leftStickX", s.LeftX, 128) || Off("leftStickY", s.LeftY, 128)
            || Off("rightStickX", s.RightX, 128) || Off("rightStickY", s.RightY, 128);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ch in _channels)
        {
            ch.Virtual?.Dispose();
            ch.Reader.Dispose();
        }
        _channels.Clear();
        _bus.Dispose();
    }
}
