using System.Text;

namespace Dovetail.Core;

/// <summary>
/// Decoder for the HIDP_PREPARSED_DATA blob that hidclass.sys returns from
/// IOCTL_HID_GET_COLLECTION_DESCRIPTOR.
///
/// Windows does not hand user mode the original HID report descriptor bytes. What comes
/// back is hidparse.sys's own resolved form, tagged "HidP KDR". That is strictly more
/// useful here: the bit and byte position of every field is already computed, which is
/// exactly what Section 2.3 asks for. The layout is not documented by Microsoft, so every
/// field this decoder reads is cross-checked against the documented HidP_Get*Caps APIs
/// before it is trusted - see <see cref="CrossCheck"/>.
///
/// Layout, confirmed against this device and against HidP_GetCaps:
///   0x00  char[8]  "HidP KDR"
///   0x08  u16      Usage                (matched HIDP_CAPS.Usage)
///   0x0A  u16      UsagePage            (matched HIDP_CAPS.UsagePage)
///   0x0C  u32      reserved
///   0x10  CapsInfo[3]                   one per report type: Input, Output, Feature
///           u16 FirstCap, u16 NumberOfCaps, u16 LastCap, u16 ReportByteLength
///                                       (ReportByteLength matched HIDP_CAPS exactly)
///   0x28  u16      FirstByteOfLinkCollectionArray  (relative to the caps array)
///   0x2A  u16      NumberLinkCollectionNodes       (matched HIDP_CAPS)
///   0x2C  Cap[]    104 bytes each
///
/// Cap, 104 bytes:
///   +0   u16 UsagePage        +2   u8  ReportID       +3   u8  BitPosition
///   +4   u16 BitSize          +6   u16 ReportCount    +8   u16 BytePosition
///   +10  u16 BitCount         +12  u32 BitField       +16  u16 NextBytePosition
///   +18  u16 LinkCollection
///   +60  u16 UsageMin         +62  u16 UsageMax
///   +80  i32 LogicalMin       +84  i32 LogicalMax
///   +88  i32 PhysicalMin      +92  i32 PhysicalMax
/// </summary>
internal static class PreparsedData
{
    internal const string Signature = "HidP KDR";

    internal sealed record CapsInfo(int FirstCap, int NumberOfCaps, int LastCap, int ReportByteLength);

    internal sealed record Cap(
        int Index,
        ushort UsagePage,
        byte ReportId,
        byte BitPosition,
        ushort BitSize,
        ushort ReportCount,
        ushort BytePosition,
        ushort BitCount,
        uint BitField,
        ushort NextBytePosition,
        ushort LinkCollection,
        ushort UsageMin,
        ushort UsageMax,
        int LogicalMin,
        int LogicalMax,
        int PhysicalMin,
        int PhysicalMax)
    {
        public bool IsButton => UsagePage == 0x09 || BitSize == 1;
        public bool IsConstant => (BitField & 0x01) != 0;
        public bool IsVariable => (BitField & 0x02) != 0;
        public bool IsRelative => (BitField & 0x04) != 0;
        public bool HasNull => (BitField & 0x40) != 0;

        public string Flags
        {
            get
            {
                var f = new List<string> { IsConstant ? "Cnst" : "Data", IsVariable ? "Var" : "Ary", IsRelative ? "Rel" : "Abs" };
                if (HasNull) f.Add("Null");
                if ((BitField & 0x08) != 0) f.Add("Wrap");
                if ((BitField & 0x10) != 0) f.Add("NonLin");
                if ((BitField & 0x20) != 0) f.Add("NoPref");
                return string.Join(",", f);
            }
        }

        public int FirstBitInReport => BytePosition * 8 + BitPosition;
        public int LastBitInReport => FirstBitInReport + BitCount - 1;

        public string UsageText => UsageMin == UsageMax
            ? HidUsages.Describe(UsagePage, UsageMin)
            : $"{HidUsages.Describe(UsagePage, UsageMin)} .. {HidUsages.Describe(UsagePage, UsageMax)}";

        /// <summary>Human-readable position, e.g. "byte 5 bits 4-7" or "byte 3".</summary>
        public string Position
        {
            get
            {
                if (BitCount >= 8 && BitPosition == 0 && BitCount % 8 == 0)
                {
                    int nb = BitCount / 8;
                    return nb == 1 ? $"byte {BytePosition}" : $"bytes {BytePosition}..{BytePosition + nb - 1}";
                }
                int end = BitPosition + BitCount - 1;
                if (end <= 7) return $"byte {BytePosition} bit{(BitCount > 1 ? "s" : "")} {BitPosition}" + (BitCount > 1 ? $"-{end}" : "");
                return $"byte {BytePosition} bit {BitPosition} onward, {BitCount} bits";
            }
        }
    }

    internal sealed class Blob
    {
        public bool Valid;
        public string Why = "";
        public ushort Usage, UsagePage;
        public CapsInfo Input = new(0, 0, 0, 0);
        public CapsInfo Output = new(0, 0, 0, 0);
        public CapsInfo Feature = new(0, 0, 0, 0);
        public int FirstByteOfLinkCollectionArray;
        public int NumberLinkCollectionNodes;
        public List<Cap> Caps = [];

        public IEnumerable<Cap> InputCaps =>
            Caps.Where(c => c.Index >= Input.FirstCap && c.Index < Input.FirstCap + Input.NumberOfCaps);
        public IEnumerable<Cap> OutputCaps =>
            Caps.Where(c => c.Index >= Output.FirstCap && c.Index < Output.FirstCap + Output.NumberOfCaps);
    }

    private const int CapsArrayOffset = 0x2C;
    private const int CapStride = 104;

    internal static Blob Parse(byte[] d)
    {
        var b = new Blob();

        if (d.Length < CapsArrayOffset)
        { b.Why = $"blob too short ({d.Length} bytes)"; return b; }

        string sig = Encoding.ASCII.GetString(d, 0, 8);
        if (sig != Signature)
        { b.Why = $"signature is \"{sig}\", expected \"{Signature}\""; return b; }

        b.Usage = BitConverter.ToUInt16(d, 0x08);
        b.UsagePage = BitConverter.ToUInt16(d, 0x0A);

        CapsInfo ReadInfo(int off) => new(
            BitConverter.ToUInt16(d, off),
            BitConverter.ToUInt16(d, off + 2),
            BitConverter.ToUInt16(d, off + 4),
            BitConverter.ToUInt16(d, off + 6));

        b.Input = ReadInfo(0x10);
        b.Output = ReadInfo(0x18);
        b.Feature = ReadInfo(0x20);
        b.FirstByteOfLinkCollectionArray = BitConverter.ToUInt16(d, 0x28);
        b.NumberLinkCollectionNodes = BitConverter.ToUInt16(d, 0x2A);

        int totalCaps = Math.Max(b.Input.FirstCap + b.Input.NumberOfCaps,
                        Math.Max(b.Output.FirstCap + b.Output.NumberOfCaps,
                                 b.Feature.FirstCap + b.Feature.NumberOfCaps));

        for (int i = 0; i < totalCaps; i++)
        {
            int o = CapsArrayOffset + i * CapStride;
            if (o + CapStride > d.Length) { b.Why = $"caps array truncated at cap {i}"; break; }

            b.Caps.Add(new Cap(
                Index: i,
                UsagePage: BitConverter.ToUInt16(d, o + 0),
                ReportId: d[o + 2],
                BitPosition: d[o + 3],
                BitSize: BitConverter.ToUInt16(d, o + 4),
                ReportCount: BitConverter.ToUInt16(d, o + 6),
                BytePosition: BitConverter.ToUInt16(d, o + 8),
                BitCount: BitConverter.ToUInt16(d, o + 10),
                BitField: BitConverter.ToUInt32(d, o + 12),
                NextBytePosition: BitConverter.ToUInt16(d, o + 16),
                LinkCollection: BitConverter.ToUInt16(d, o + 18),
                UsageMin: BitConverter.ToUInt16(d, o + 60),
                UsageMax: BitConverter.ToUInt16(d, o + 62),
                LogicalMin: BitConverter.ToInt32(d, o + 80),
                LogicalMax: BitConverter.ToInt32(d, o + 84),
                PhysicalMin: BitConverter.ToInt32(d, o + 88),
                PhysicalMax: BitConverter.ToInt32(d, o + 92)));
        }

        b.Valid = b.Caps.Count > 0;
        if (b.Valid) b.Why = "ok";
        return b;
    }

    /// <summary>
    /// Validates the decoded blob against the documented HidP_* APIs. Any disagreement
    /// means the undocumented layout must not be trusted on this Windows build.
    /// </summary>
    internal static List<string> CrossCheck(Blob b, HidCollectionInfo info)
    {
        var lines = new List<string>();
        void Check(string what, object fromBlob, object fromApi) =>
            lines.Add($"    {(Equals(fromBlob.ToString(), fromApi.ToString()) ? "AGREE   " : "MISMATCH")} " +
                      $"{what,-34} blob={fromBlob,-18} HidP_Get*Caps={fromApi}");

        Check("top-level Usage", $"0x{b.Usage:X4}", $"0x{info.Caps.Usage:X4}");
        Check("top-level UsagePage", $"0x{b.UsagePage:X4}", $"0x{info.Caps.UsagePage:X4}");
        Check("InputReportByteLength", b.Input.ReportByteLength, info.Caps.InputReportByteLength);
        Check("OutputReportByteLength", b.Output.ReportByteLength, info.Caps.OutputReportByteLength);
        Check("FeatureReportByteLength", b.Feature.ReportByteLength, info.Caps.FeatureReportByteLength);
        Check("NumberLinkCollectionNodes", b.NumberLinkCollectionNodes, info.Caps.NumberLinkCollectionNodes);

        int blobInputCaps = b.Input.NumberOfCaps;
        int apiInputCaps = info.Caps.NumberInputButtonCaps + info.Caps.NumberInputValueCaps;
        Check("input cap count", blobInputCaps, apiInputCaps);

        // per-axis comparison against HidP_GetValueCaps
        foreach (var v in info.ValueCaps)
        {
            ushort usage = v.UsageMin;
            var match = b.InputCaps.FirstOrDefault(c =>
                c.UsagePage == v.UsagePage && c.UsageMin == usage && !c.IsButton);
            if (match is null)
            {
                lines.Add($"    MISSING  value cap {HidUsages.Describe(v.UsagePage, usage),-14} " +
                          "present in HidP_GetValueCaps but not found in blob");
                continue;
            }
            Check($"{HidUsages.Describe(v.UsagePage, usage)} BitSize", match.BitSize, v.BitSize);
            Check($"{HidUsages.Describe(v.UsagePage, usage)} ReportCount", match.ReportCount, v.ReportCount);
            Check($"{HidUsages.Describe(v.UsagePage, usage)} LogicalMin", match.LogicalMin, v.LogicalMin);
            Check($"{HidUsages.Describe(v.UsagePage, usage)} LogicalMax", match.LogicalMax, v.LogicalMax);
            Check($"{HidUsages.Describe(v.UsagePage, usage)} ReportID", match.ReportId, v.ReportID);
        }

        foreach (var bc in info.ButtonCaps)
        {
            var match = b.InputCaps.FirstOrDefault(c =>
                c.UsagePage == bc.UsagePage && c.UsageMin == bc.UsageMin && c.UsageMax == bc.UsageMax);
            if (match is null)
            {
                lines.Add($"    MISSING  button cap {HidUsages.Describe(bc.UsagePage, bc.UsageMin)}" +
                          $"..{bc.UsageMax} present in HidP_GetButtonCaps but not found in blob");
                continue;
            }
            Check($"buttons {bc.UsageMin}-{bc.UsageMax} ReportID", match.ReportId, bc.ReportID);
            Check($"buttons {bc.UsageMin}-{bc.UsageMax} LinkColl", match.LinkCollection, bc.LinkCollection);
        }

        return lines;
    }

    /// <summary>
    /// Rebuilds an equivalent HID report descriptor in the conventional textual form, so the
    /// captured layout can be read the way a descriptor listing normally is. Reconstructed
    /// from the decoded caps, not the original bytes.
    /// </summary>
    internal static string ReconstructDescriptor(Blob b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Usage Page ({HidUsages.PageName(b.UsagePage)})");
        sb.AppendLine($"Usage ({HidUsages.Describe(b.UsagePage, b.Usage)})");
        sb.AppendLine("Collection (Application)");

        byte? currentReport = null;
        ushort currentPage = 0xFFFF;

        foreach (var c in b.InputCaps.OrderBy(c => c.FirstBitInReport))
        {
            if (currentReport != c.ReportId)
            { sb.AppendLine($"    Report ID ({c.ReportId})"); currentReport = c.ReportId; }
            if (currentPage != c.UsagePage)
            { sb.AppendLine($"    Usage Page ({HidUsages.PageName(c.UsagePage)})"); currentPage = c.UsagePage; }

            if (c.UsageMin == c.UsageMax)
                sb.AppendLine($"    Usage ({HidUsages.Describe(c.UsagePage, c.UsageMin)})");
            else
            {
                sb.AppendLine($"    Usage Minimum ({HidUsages.Describe(c.UsagePage, c.UsageMin)})");
                sb.AppendLine($"    Usage Maximum ({HidUsages.Describe(c.UsagePage, c.UsageMax)})");
            }
            sb.AppendLine($"    Logical Minimum ({c.LogicalMin})");
            sb.AppendLine($"    Logical Maximum ({c.LogicalMax})");
            sb.AppendLine($"    Report Size ({c.BitSize})");
            sb.AppendLine($"    Report Count ({c.ReportCount})");
            sb.AppendLine($"    Input ({c.Flags})            ; {c.Position}");
        }
        sb.AppendLine("End Collection");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Byte-by-byte picture of one report, built from the cap bit positions.</summary>
    internal static string ReportMap(Blob b, byte reportId, bool input = true)
    {
        var caps = (input ? b.InputCaps : b.OutputCaps).Where(c => c.ReportId == reportId).ToList();
        if (caps.Count == 0) return "(no caps for this report id)";

        int reportBytes = input ? b.Input.ReportByteLength : b.Output.ReportByteLength;
        var sb = new StringBuilder();
        sb.AppendLine($"  byte | bits     | field");
        sb.AppendLine($"  -----+----------+---------------------------------------------------------");

        // byte 0 carries the report id when the device numbers its reports
        bool hasReportId = caps.Min(c => c.BytePosition) > 0;
        if (hasReportId)
            sb.AppendLine($"     0 | 0-7      | Report ID = {reportId}");

        for (int by = hasReportId ? 1 : 0; by < reportBytes; by++)
        {
            var here = caps.Where(c => c.BytePosition <= by &&
                                       by <= (c.FirstBitInReport + c.BitCount - 1) / 8)
                           .OrderBy(c => c.FirstBitInReport).ToList();
            if (here.Count == 0)
            {
                sb.AppendLine($"  {by,4} | 0-7      | (not described by any cap)");
                continue;
            }
            foreach (var c in here)
            {
                int startBit = Math.Max(c.FirstBitInReport, by * 8) - by * 8;
                int endBit = Math.Min(c.LastBitInReport, by * 8 + 7) - by * 8;
                string bits = startBit == endBit ? $"{startBit}" : $"{startBit}-{endBit}";

                string label;
                if (c.UsageMin != c.UsageMax && c.BitSize == 1)
                {
                    // one bit per usage: name the exact usages landing in this byte
                    int firstUsage = c.UsageMin + (by * 8 + startBit - c.FirstBitInReport);
                    int lastUsage = c.UsageMin + (by * 8 + endBit - c.FirstBitInReport);
                    label = firstUsage == lastUsage
                        ? HidUsages.Describe(c.UsagePage, (ushort)firstUsage)
                        : $"{HidUsages.Describe(c.UsagePage, (ushort)firstUsage)} .. {HidUsages.Describe(c.UsagePage, (ushort)lastUsage)}";
                }
                else
                {
                    label = c.UsageText;
                }
                string range = c.IsButton ? "" : $"  [{c.LogicalMin}..{c.LogicalMax}]";
                sb.AppendLine($"  {by,4} | {bits,-8} | {label}{range}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
