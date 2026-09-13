using System.Text;

using Dovetail.Core;

namespace Dovetail.Diagnostics;

internal static class Program
{
    // The controller identified in Stage 0: Shanwan "Twin USB Joystick".
    private const ushort TargetVid = 0x0810;
    private const ushort TargetPid = 0x0001;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "dump";

        try
        {
            return cmd switch
            {
                "dump" => CmdDump(args),
                "watch" => CmdWatch(args),
                "sweep" => Sweep.Run(args, TargetVid, TargetPid),
                "calibrate" => Calibrate.Run(args, TargetVid, TargetPid),
                "identify" => Identify.Run(args, TargetVid, TargetPid),
                "-h" or "--help" or "help" => Usage(),
                _ => Usage($"unknown command '{cmd}'")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    private static int Usage(string? problem = null)
    {
        if (problem is not null) Console.Error.WriteLine($"{problem}\n");
        Console.WriteLine("""
            dovetail-diag - Dovetail diagnostic module (Section 5.1)

              dump  [--all] [--out <file>]   device identity, raw report descriptor, parsed caps
              watch [--col COL01|COL02] [--seconds N]
                                             live raw report bytes, changed bytes highlighted
              sweep [--out <file>] [--only id,id] [--step-timeout N]
                                             guided input sweep; records the exact byte and bit
                                             that changes for every physical input
              calibrate [--out <file>] [--only rest,leftstick,rightstick,triggers,clicks]
                                             guided calibration: rest noise, stick range,
                                             triggers, and the L3/R3 isolation test
              identify [--seconds N] [--profile <file>] [--out <file>]
                                             names whatever you press, live; use it when a
                                             physical button and its label disagree

            With no arguments, 'dump' runs against VID 0x0810 / PID 0x0001.
            """);
        return problem is null ? 0 : 1;
    }

    // ---------------------------------------------------------------- dump

    private static int CmdDump(string[] args)
    {
        bool all = args.Contains("--all");
        string? outPath = ArgValue(args, "--out");

        var sw = new StringWriter();
        void W(string s = "") { Console.WriteLine(s); sw.WriteLine(s); }

        W($"dovetail-diag dump   {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        W($"host: {Environment.MachineName}   os: {Environment.OSVersion.VersionString}   64-bit: {Environment.Is64BitProcess}");
        W();

        var devices = all ? HidScan.Enumerate() : HidScan.Enumerate(TargetVid, TargetPid);

        if (devices.Count == 0)
        {
            W(all
                ? "No HID interfaces found at all."
                : $"No HID interface present for VID 0x{TargetVid:X4} / PID 0x{TargetPid:X4}.");
            W("Connect the controller and run again.");
            if (outPath is not null) File.WriteAllText(outPath, sw.ToString());
            return 1;
        }

        W($"Found {devices.Count} HID interface(s)" +
          (all ? "" : $" for VID 0x{TargetVid:X4} / PID 0x{TargetPid:X4}") + ".");
        W();

        int n = 0;
        foreach (var d in devices.OrderBy(x => x.InstanceId, StringComparer.OrdinalIgnoreCase))
        {
            n++;
            W(new string('=', 78));
            W($"[{n}] {(d.CollectionTag.Length > 0 ? d.CollectionTag : "collection")}  {d.DeviceDesc}");
            W(new string('=', 78));
            W($"  instance id   : {d.InstanceId}");
            W($"  device path   : {d.DevicePath}");
            W($"  hardware ids  : {d.HardwareIds}");
            W($"  service       : {(d.Service.Length > 0 ? d.Service : "(none)")}");
            W($"  upper filters : {(d.UpperFilters.Length > 0 ? d.UpperFilters : "(none)")}");
            W($"  lower filters : {(d.LowerFilters.Length > 0 ? d.LowerFilters : "(none)")}");
            W($"  handle        : {d.OpenDiagnostic}");
            W();
            W($"  VID / PID     : 0x{d.Vid:X4} / 0x{d.Pid:X4}   (decimal {d.Vid} / {d.Pid})");
            W($"  version       : 0x{d.Version:X4}");
            W($"  manufacturer  : {(d.Manufacturer.Length > 0 ? d.Manufacturer : "(not reported)")}");
            W($"  product       : {(d.Product.Length > 0 ? d.Product : "(not reported)")}");
            W($"  serial        : {(d.Serial.Length > 0 ? d.Serial : "(not reported)")}");
            W();
            W($"  top-level usage      : {HidUsages.Describe(d.Caps.UsagePage, d.Caps.Usage)} " +
              $"(page 0x{d.Caps.UsagePage:X4}, usage 0x{d.Caps.Usage:X4})");
            W($"  input report length  : {d.Caps.InputReportByteLength} bytes");
            W($"  output report length : {d.Caps.OutputReportByteLength} bytes");
            W($"  feature report length: {d.Caps.FeatureReportByteLength} bytes");
            W($"  link collections     : {d.Caps.NumberLinkCollectionNodes}");
            W($"  input button caps    : {d.Caps.NumberInputButtonCaps}");
            W($"  input value caps     : {d.Caps.NumberInputValueCaps}");
            W($"  input data indices   : {d.Caps.NumberInputDataIndices}");
            W();

            W("  --- HID DESCRIPTOR DATA FROM hidclass.sys ---");
            W($"  retrieval: {d.DescriptorDiagnostic}");
            if (d.RawReportDescriptor is { Length: > 0 } raw)
            {
                bool isPreparsed = raw.Length >= 8 &&
                    Encoding.ASCII.GetString(raw, 0, 8) == PreparsedData.Signature;

                W($"  {raw.Length} bytes, signature \"{(raw.Length >= 8 ? Encoding.ASCII.GetString(raw, 0, 8) : "?")}\"");
                W(isPreparsed
                    ? "  NOTE: IOCTL_HID_GET_COLLECTION_DESCRIPTOR returns hidparse.sys's resolved"
                    : "  NOTE: unrecognised format - treating as opaque.");
                if (isPreparsed)
                {
                    W("        HIDP_PREPARSED_DATA, not the original report descriptor bytes. Windows");
                    W("        does not expose the original bytes to user mode. The resolved form");
                    W("        carries the bit and byte position of every field, which is what");
                    W("        Section 2.3 requires, and every field read below is cross-checked");
                    W("        against the documented HidP_Get*Caps APIs.");
                }
                W();
                W("  --- BLOB HEX ---");
                foreach (var line in ReportDescriptorParser.HexDump(raw).Split('\n'))
                    W("    " + line.TrimEnd());
                W();

                if (isPreparsed)
                {
                    var blob = PreparsedData.Parse(raw);
                    W("  --- PREPARSED HEADER ---");
                    W($"    decode status : {blob.Why}");
                    W($"    top-level     : {HidUsages.Describe(blob.UsagePage, blob.Usage)}");
                    W($"    input   caps {blob.Input.NumberOfCaps,3} starting at {blob.Input.FirstCap,3}   report length {blob.Input.ReportByteLength} bytes");
                    W($"    output  caps {blob.Output.NumberOfCaps,3} starting at {blob.Output.FirstCap,3}   report length {blob.Output.ReportByteLength} bytes");
                    W($"    feature caps {blob.Feature.NumberOfCaps,3} starting at {blob.Feature.FirstCap,3}   report length {blob.Feature.ReportByteLength} bytes");
                    W($"    link collection array at caps+0x{blob.FirstByteOfLinkCollectionArray:X}, {blob.NumberLinkCollectionNodes} nodes");
                    W();

                    W("  --- CROSS-CHECK: undocumented blob vs documented HidP_Get*Caps ---");
                    var checks = PreparsedData.CrossCheck(blob, d);
                    foreach (var line in checks) W(line);
                    int mismatches = checks.Count(l => l.Contains("MISMATCH") || l.Contains("MISSING"));
                    W();
                    W(mismatches == 0
                        ? $"    RESULT: all {checks.Count} checks agree. Bit positions below are trustworthy."
                        : $"    RESULT: {mismatches} of {checks.Count} checks disagree. DO NOT trust bit positions; use the sweep.");
                    W();

                    W("  --- INPUT CAPS WITH EXACT BIT POSITIONS ---");
                    W("    idx rpt  position                  size cnt  logical      flags       usage");
                    W("    --- ---  ------------------------  ---- ---  -----------  ----------  ------------------------");
                    foreach (var c in blob.InputCaps.OrderBy(c => c.FirstBitInReport))
                        W($"    {c.Index,3} {c.ReportId,3}  {c.Position,-24}  {c.BitSize,4} {c.ReportCount,3}  " +
                          $"{c.LogicalMin,4}..{c.LogicalMax,-5}  {c.Flags,-10}  {c.UsageText}");
                    W();

                    foreach (byte rid in blob.InputCaps.Select(c => c.ReportId).Distinct().OrderBy(x => x))
                    {
                        W($"  --- INPUT REPORT {rid} BYTE MAP ({blob.Input.ReportByteLength} bytes on the wire) ---");
                        foreach (var line in PreparsedData.ReportMap(blob, rid).Split('\n'))
                            W("  " + line.TrimEnd());
                        W();
                    }

                    if (blob.OutputCaps.Any())
                    {
                        W("  --- OUTPUT CAPS (rumble / LED channel) ---");
                        foreach (var c in blob.OutputCaps.OrderBy(c => c.FirstBitInReport))
                            W($"    {c.Index,3} {c.ReportId,3}  {c.Position,-24}  {c.BitSize,4} {c.ReportCount,3}  " +
                              $"{c.LogicalMin,4}..{c.LogicalMax,-5}  {c.Flags,-10}  {c.UsageText}");
                        W();
                    }

                    W("  --- RECONSTRUCTED REPORT DESCRIPTOR (equivalent form) ---");
                    foreach (var line in PreparsedData.ReconstructDescriptor(blob).Split('\n'))
                        W("    " + line.TrimEnd());
                    W();
                }
            }
            else
            {
                W("  NOT RETRIEVED - falling back to the parsed capability tables below.");
            }
            W();

            W("  --- PARSED VALUE CAPS (axes and hats, from HidP_GetValueCaps) ---");
            if (d.ValueCaps.Length == 0) W("    (none)");
            foreach (var v in d.ValueCaps)
            {
                string u = v.IsRange != 0
                    ? $"{HidUsages.Describe(v.UsagePage, v.UsageMin)} .. {HidUsages.Describe(v.UsagePage, v.UsageMax)}"
                    : HidUsages.Describe(v.UsagePage, v.UsageMin);
                W($"    rpt {v.ReportID}  linkColl {v.LinkCollection,2}  {v.BitSize,2} bit x{v.ReportCount}  " +
                  $"logical {v.LogicalMin}..{v.LogicalMax}  physical {v.PhysicalMin}..{v.PhysicalMax}  " +
                  $"{(v.IsAbsolute != 0 ? "abs" : "rel")}{(v.HasNull != 0 ? " hasNull" : "")}  {u}");
            }
            W();

            W("  --- PARSED BUTTON CAPS (from HidP_GetButtonCaps) ---");
            if (d.ButtonCaps.Length == 0) W("    (none)");
            foreach (var b in d.ButtonCaps)
            {
                string u = b.IsRange != 0
                    ? $"{HidUsages.Describe(b.UsagePage, b.UsageMin)} .. {HidUsages.Describe(b.UsagePage, b.UsageMax)}"
                    : HidUsages.Describe(b.UsagePage, b.UsageMin);
                string di = b.IsRange != 0
                    ? $"dataIndex {b.DataIndexMin}..{b.DataIndexMax}"
                    : $"dataIndex {b.DataIndexMin}";
                W($"    rpt {b.ReportID}  linkColl {b.LinkCollection,2}  {di,-26}  {u}");
            }
            W();

            W("  --- LINK COLLECTION TREE ---");
            if (d.LinkCollections.Length == 0) W("    (none)");
            for (int k = 0; k < d.LinkCollections.Length; k++)
            {
                var c = d.LinkCollections[k];
                W($"    [{k}] {HidUsages.CollectionTypeName(c.CollectionType),-12} " +
                  $"{HidUsages.Describe(c.LinkUsagePage, c.LinkUsage),-22} " +
                  $"parent={c.Parent} children={c.NumberOfChildren} " +
                  $"firstChild={c.FirstChild} nextSibling={c.NextSibling}");
            }
            W();
        }

        if (outPath is not null)
        {
            File.WriteAllText(outPath, sw.ToString());
            Console.WriteLine($"(written to {outPath})");
        }
        return 0;
    }

    // ---------------------------------------------------------------- watch

    private static int CmdWatch(string[] args)
    {
        string? col = ArgValue(args, "--col");
        int seconds = int.TryParse(ArgValue(args, "--seconds"), out int s) ? s : 20;

        var devices = HidScan.Enumerate(TargetVid, TargetPid)
            .Where(d => col is null || d.CollectionTag.Equals(col, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No matching collection found. Is the controller connected?");
            return 1;
        }

        Console.WriteLine($"Watching {devices.Count} collection(s) for {seconds}s. Move and press everything.");
        Console.WriteLine("Changed bytes are marked with ^. Press Ctrl+C to stop early.");
        Console.WriteLine();

        var readers = new List<(HidCollectionInfo info, HidReader reader, byte[]? last)>();
        try
        {
            foreach (var d in devices)
            {
                try
                {
                    var r = new HidReader(d.DevicePath, d.Caps.InputReportByteLength);
                    readers.Add((d, r, null));
                    Console.WriteLine($"  {d.CollectionTag}: open, {r.ReportLength}-byte reports");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {d.CollectionTag}: cannot open - {ex.Message}");
                }
            }
            Console.WriteLine();

            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var counts = new int[readers.Count];

            while (DateTime.UtcNow < deadline)
            {
                for (int i = 0; i < readers.Count; i++)
                {
                    var (info, reader, last) = readers[i];
                    byte[]? rpt;
                    try { rpt = reader.Read(30); }
                    catch (Exception ex) { Console.WriteLine($"{info.CollectionTag}: read error {ex.Message}"); continue; }
                    if (rpt is null || rpt.Length == 0) continue;

                    counts[i]++;
                    string hexLine = string.Join(" ", rpt.Select(b => b.ToString("X2")));
                    string marks = last is null
                        ? new string(' ', hexLine.Length)
                        : string.Join(" ", rpt.Select((b, k) =>
                            k < last.Length && last[k] != b ? "^^" : "  "));

                    Console.WriteLine($"{info.CollectionTag} #{counts[i],-5} {hexLine}");
                    if (last is not null && marks.Trim().Length > 0)
                        Console.WriteLine($"{new string(' ', info.CollectionTag.Length + 8)} {marks}");

                    readers[i] = (info, reader, rpt);
                }
            }

            Console.WriteLine();
            for (int i = 0; i < readers.Count; i++)
                Console.WriteLine($"  {readers[i].info.CollectionTag}: {counts[i]} reports received");
        }
        finally
        {
            foreach (var (_, r, _) in readers) r.Dispose();
        }
        return 0;
    }

    // ---------------------------------------------------------------- helpers

    internal static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
