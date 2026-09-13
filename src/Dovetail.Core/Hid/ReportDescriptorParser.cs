using System.Text;

namespace Dovetail.Core;

/// <summary>One Input/Output/Feature field, with the bit position it occupies in its report.</summary>
internal sealed record HidField(
    char ReportType,        // I, O or F
    byte ReportId,
    int BitOffset,          // from the first bit after the report id byte
    int BitSize,
    int ReportCount,
    ushort UsagePage,
    ushort UsageMin,
    ushort UsageMax,
    int LogicalMin,
    int LogicalMax,
    bool IsConstant,
    bool IsVariable,
    bool IsRelative,
    string CollectionPath)
{
    public int TotalBits => BitSize * ReportCount;
    public int ByteOffset => BitOffset / 8;
    public int BitInByte => BitOffset % 8;

    public string UsageText => UsageMin == UsageMax
        ? HidUsages.Describe(UsagePage, UsageMin)
        : $"{HidUsages.Describe(UsagePage, UsageMin)} .. {HidUsages.Describe(UsagePage, UsageMax)}";
}

/// <summary>
/// Decodes a raw HID report descriptor into its item stream and a flat field list with
/// exact bit offsets. Implements the item encoding from the USB HID 1.11 specification,
/// section 6.2.2, including Push/Pop of the global item state.
/// </summary>
internal static class ReportDescriptorParser
{
    private sealed class GlobalState
    {
        public ushort UsagePage;
        public int LogicalMin, LogicalMax;
        public int PhysicalMin, PhysicalMax;
        public int UnitExp, Unit;
        public int ReportSize, ReportCount;
        public byte ReportId;
        public GlobalState Clone() => (GlobalState)MemberwiseClone();
    }

    internal sealed class Result
    {
        public List<string> ItemLines = [];
        public List<HidField> Fields = [];
        public Dictionary<byte, int> InputBitsByReportId = [];
        public bool UsesReportIds;
        public List<string> Warnings = [];
    }

    internal static Result Parse(byte[] d)
    {
        var r = new Result();
        var g = new GlobalState();
        var stack = new Stack<GlobalState>();
        var usages = new List<ushort>();
        ushort usageMinLocal = 0, usageMaxLocal = 0;
        bool haveUsageMin = false, haveUsageMax = false;
        var collectionPath = new List<string>();

        // running bit position per report id, per report type
        var inBits = new Dictionary<byte, int>();
        var outBits = new Dictionary<byte, int>();
        var featBits = new Dictionary<byte, int>();

        int i = 0;
        int indent = 0;

        while (i < d.Length)
        {
            byte prefix = d[i];
            int bSize = prefix & 0x03;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;
            int dataLen = bSize == 3 ? 4 : bSize;
            int itemStart = i;
            i++;

            if (bType == 3 && bTag == 0x0F)
            {
                // long item: [bDataSize][bLongItemTag][data...]
                if (i + 2 > d.Length) { r.Warnings.Add($"truncated long item at byte {itemStart}"); break; }
                int longSize = d[i]; int longTag = d[i + 1];
                i += 2 + longSize;
                r.ItemLines.Add($"{new string(' ', indent * 2)}Long item tag 0x{longTag:X2}, {longSize} data bytes (not interpreted)");
                continue;
            }

            if (i + dataLen > d.Length) { r.Warnings.Add($"truncated item at byte {itemStart}"); break; }

            uint raw = 0;
            for (int k = 0; k < dataLen; k++) raw |= (uint)d[i + k] << (8 * k);
            i += dataLen;

            int signed = dataLen switch
            {
                1 => (sbyte)raw,
                2 => (short)raw,
                4 => (int)raw,
                _ => 0
            };

            string hex = string.Join(" ", d.Skip(itemStart).Take(i - itemStart).Select(b => b.ToString("X2")));
            void Emit(string text) =>
                r.ItemLines.Add($"{hex,-14} {new string(' ', indent * 2)}{text}");

            switch (bType)
            {
                case 0: // Main
                    switch (bTag)
                    {
                        case 0x8: // Input
                        case 0x9: // Output
                        case 0xA: // Feature
                            {
                                char rt = bTag == 0x8 ? 'I' : bTag == 0x9 ? 'O' : 'F';
                                var bits = bTag == 0x8 ? inBits : bTag == 0x9 ? outBits : featBits;
                                bits.TryAdd(g.ReportId, 0);
                                int offset = bits[g.ReportId];

                                bool isConstant = (raw & 0x01) != 0;
                                bool isVariable = (raw & 0x02) != 0;
                                bool isRelative = (raw & 0x04) != 0;

                                // An array item (not variable) carries ReportCount indices;
                                // a variable item carries ReportCount separate fields.
                                ushort uMin, uMax;
                                if (haveUsageMin || haveUsageMax)
                                {
                                    uMin = usageMinLocal; uMax = usageMaxLocal;
                                }
                                else if (usages.Count > 0)
                                {
                                    uMin = usages[0];
                                    uMax = usages[^1];
                                }
                                else { uMin = 0; uMax = 0; }

                                string path = collectionPath.Count == 0 ? "(root)" : string.Join(" / ", collectionPath);

                                if (isVariable && usages.Count > 1 && !haveUsageMin && !haveUsageMax
                                    && usages.Count == g.ReportCount)
                                {
                                    // one field per declared usage: split so each axis gets its own offset
                                    for (int u = 0; u < usages.Count; u++)
                                    {
                                        r.Fields.Add(new HidField(rt, g.ReportId,
                                            offset + u * g.ReportSize, g.ReportSize, 1,
                                            g.UsagePage, usages[u], usages[u],
                                            g.LogicalMin, g.LogicalMax,
                                            isConstant, isVariable, isRelative, path));
                                    }
                                }
                                else
                                {
                                    r.Fields.Add(new HidField(rt, g.ReportId, offset,
                                        g.ReportSize, g.ReportCount, g.UsagePage, uMin, uMax,
                                        g.LogicalMin, g.LogicalMax,
                                        isConstant, isVariable, isRelative, path));
                                }

                                bits[g.ReportId] = offset + g.ReportSize * g.ReportCount;

                                var flags = new List<string> { isConstant ? "Cnst" : "Data", isVariable ? "Var" : "Ary", isRelative ? "Rel" : "Abs" };
                                if ((raw & 0x08) != 0) flags.Add("Wrap");
                                if ((raw & 0x10) != 0) flags.Add("NonLin");
                                if ((raw & 0x20) != 0) flags.Add("NoPref");
                                if ((raw & 0x40) != 0) flags.Add("Null");
                                if ((raw & 0x100) != 0) flags.Add("BufBytes");
                                string name = bTag == 0x8 ? "Input" : bTag == 0x9 ? "Output" : "Feature";
                                Emit($"{name} ({string.Join(",", flags)})  " +
                                     $"[report {g.ReportId}] bits {offset}..{offset + g.ReportSize * g.ReportCount - 1} " +
                                     $"({g.ReportCount} x {g.ReportSize} bit)");
                                break;
                            }
                        case 0xB: // Collection
                            {
                                string ctype = HidUsages.CollectionTypeName((byte)raw);
                                ushort cu = usages.Count > 0 ? usages[^1] : (ushort)0;
                                string label = $"{ctype}:{HidUsages.Describe(g.UsagePage, cu)}";
                                Emit($"Collection ({label})");
                                collectionPath.Add(label);
                                indent++;
                                break;
                            }
                        case 0xC: // End Collection
                            if (indent > 0) indent--;
                            if (collectionPath.Count > 0) collectionPath.RemoveAt(collectionPath.Count - 1);
                            Emit("End Collection");
                            break;
                        default:
                            Emit($"Main tag 0x{bTag:X1} data 0x{raw:X}");
                            break;
                    }
                    // Main items clear local state
                    usages.Clear();
                    haveUsageMin = haveUsageMax = false;
                    break;

                case 1: // Global
                    switch (bTag)
                    {
                        case 0x0: g.UsagePage = (ushort)raw; Emit($"Usage Page ({HidUsages.PageName((ushort)raw)})"); break;
                        case 0x1: g.LogicalMin = signed; Emit($"Logical Minimum ({signed})"); break;
                        case 0x2:
                            // Logical Maximum is unsigned when Logical Minimum is non-negative
                            g.LogicalMax = g.LogicalMin >= 0 ? (int)raw : signed;
                            Emit($"Logical Maximum ({g.LogicalMax})"); break;
                        case 0x3: g.PhysicalMin = signed; Emit($"Physical Minimum ({signed})"); break;
                        case 0x4: g.PhysicalMax = g.PhysicalMin >= 0 ? (int)raw : signed; Emit($"Physical Maximum ({g.PhysicalMax})"); break;
                        case 0x5: g.UnitExp = signed; Emit($"Unit Exponent ({signed})"); break;
                        case 0x6: g.Unit = (int)raw; Emit($"Unit (0x{raw:X})"); break;
                        case 0x7: g.ReportSize = (int)raw; Emit($"Report Size ({raw})"); break;
                        case 0x8: g.ReportId = (byte)raw; r.UsesReportIds = true; Emit($"Report ID ({raw})"); break;
                        case 0x9: g.ReportCount = (int)raw; Emit($"Report Count ({raw})"); break;
                        case 0xA: stack.Push(g.Clone()); Emit("Push"); break;
                        case 0xB:
                            if (stack.Count > 0) g = stack.Pop();
                            else r.Warnings.Add("Pop with empty state stack");
                            Emit("Pop"); break;
                        default: Emit($"Global tag 0x{bTag:X1} data 0x{raw:X}"); break;
                    }
                    break;

                case 2: // Local
                    switch (bTag)
                    {
                        case 0x0: usages.Add((ushort)raw); Emit($"Usage ({HidUsages.Describe(g.UsagePage, (ushort)raw)})"); break;
                        case 0x1: usageMinLocal = (ushort)raw; haveUsageMin = true; Emit($"Usage Minimum ({HidUsages.Describe(g.UsagePage, (ushort)raw)})"); break;
                        case 0x2: usageMaxLocal = (ushort)raw; haveUsageMax = true; Emit($"Usage Maximum ({HidUsages.Describe(g.UsagePage, (ushort)raw)})"); break;
                        case 0x3: Emit($"Designator Index ({raw})"); break;
                        case 0x4: Emit($"Designator Minimum ({raw})"); break;
                        case 0x5: Emit($"Designator Maximum ({raw})"); break;
                        case 0x7: Emit($"String Index ({raw})"); break;
                        case 0x8: Emit($"String Minimum ({raw})"); break;
                        case 0x9: Emit($"String Maximum ({raw})"); break;
                        case 0xA: Emit($"Delimiter ({raw})"); break;
                        default: Emit($"Local tag 0x{bTag:X1} data 0x{raw:X}"); break;
                    }
                    break;

                default:
                    Emit($"Reserved item tag 0x{bTag:X1}");
                    break;
            }
        }

        r.InputBitsByReportId = inBits;
        return r;
    }

    internal static string HexDump(byte[] d, int perLine = 16)
    {
        var sb = new StringBuilder();
        for (int o = 0; o < d.Length; o += perLine)
        {
            int n = Math.Min(perLine, d.Length - o);
            sb.Append($"{o:X4}  ");
            for (int k = 0; k < perLine; k++)
                sb.Append(k < n ? d[o + k].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (int k = 0; k < n; k++)
                sb.Append(d[o + k] >= 0x20 && d[o + k] < 0x7F ? (char)d[o + k] : '.');
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
