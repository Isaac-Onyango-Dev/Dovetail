using System.Runtime.InteropServices;
using System.Text;

namespace Dovetail.Core;

public sealed class HidCollectionInfo
{
    public string DevicePath = "";
    public string InstanceId = "";
    public string DeviceDesc = "";
    public string HardwareIds = "";
    public string Service = "";
    public string UpperFilters = "";
    public string LowerFilters = "";
    public ushort Vid, Pid, Version;
    public string Manufacturer = "";
    public string Product = "";
    public string Serial = "";
    public Native.HIDP_CAPS Caps;
    public Native.HIDP_BUTTON_CAPS[] ButtonCaps = [];
    public Native.HIDP_VALUE_CAPS[] ValueCaps = [];
    public Native.HIDP_LINK_COLLECTION_NODE[] LinkCollections = [];
    public byte[]? RawReportDescriptor;
    public string DescriptorDiagnostic = "";
    public bool Opened;
    public string OpenDiagnostic = "";

    /// <summary>COL01 / COL02 / ... as it appears in the device instance id, else "".</summary
    public string CollectionTag
    {
        get
        {
            int i = InstanceId.IndexOf("&COL", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? "" : InstanceId.Substring(i + 1, 5).ToUpperInvariant();
        }
    }
}

public static class HidScan
{
    /// <summary>
    /// Enumerates present HID interfaces. When <paramref name="vid"/>/<paramref name="pid"/>
    /// are supplied, only matching devices are returned and each is opened for reading so
    /// its capabilities and raw descriptor can be captured.
    /// </summary>
    internal static List<HidCollectionInfo> Enumerate(ushort? vid = null, ushort? pid = null)
    {
        var results = new List<HidCollectionInfo>();
        Native.HidD_GetHidGuid(out Guid hidGuid);

        IntPtr set = Native.SetupDiGetClassDevs(
            ref hidGuid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            throw new InvalidOperationException(
                $"SetupDiGetClassDevs failed, Win32 error {Marshal.GetLastWin32Error()}");

        try
        {
            var did = new Native.SP_DEVICE_INTERFACE_DATA
            { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };

            for (int index = 0; ; index++)
            {
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did))
                    break;

                var devInfo = new Native.SP_DEVINFO_DATA
                { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };

                Native.SetupDiGetDeviceInterfaceDetail(
                    set, ref did, IntPtr.Zero, 0, out int required, ref devInfo);
                if (required <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64 (4-byte int + 2-byte
                    // WCHAR + 2 bytes padding), not the full allocation size.
                    Marshal.WriteInt32(detail, 0, 8);
                    devInfo.cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>();
                    if (!Native.SetupDiGetDeviceInterfaceDetail(
                            set, ref did, detail, required, out _, ref devInfo))
                        continue;

                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0) continue;

                    if (vid.HasValue &&
                        !path.Contains($"vid_{vid.Value:x4}", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (pid.HasValue &&
                        !path.Contains($"pid_{pid.Value:x4}", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var info = new HidCollectionInfo
                    {
                        DevicePath = path,
                        InstanceId = GetInstanceId(set, ref devInfo),
                        DeviceDesc = GetStringProp(set, ref devInfo, Native.SPDRP_DEVICEDESC),
                        HardwareIds = GetMultiStringProp(set, ref devInfo, Native.SPDRP_HARDWAREID),
                        Service = GetStringProp(set, ref devInfo, Native.SPDRP_SERVICE),
                        UpperFilters = GetMultiStringProp(set, ref devInfo, Native.SPDRP_UPPERFILTERS),
                        LowerFilters = GetMultiStringProp(set, ref devInfo, Native.SPDRP_LOWERFILTERS),
                    };

                    Populate(info);
                    results.Add(info);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }

        return results;
    }

    private static void Populate(HidCollectionInfo info)
    {
        // Read access is wanted, but fall back to query-only access if something else holds
        // the device, so the audit still reports its identity.
        IntPtr h = Native.CreateFile(info.DevicePath,
            Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

        if (h == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            h = Native.CreateFile(info.DevicePath,
                0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            info.OpenDiagnostic = h == new IntPtr(-1)
                ? $"cannot open at all (GENERIC_READ error {err}, query-only error {Marshal.GetLastWin32Error()})"
                : $"query-only handle; GENERIC_READ refused with Win32 error {err}";
        }
        else
        {
            info.Opened = true;
            info.OpenDiagnostic = "opened GENERIC_READ";
        }

        if (h == new IntPtr(-1)) return;

        try
        {
            var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (Native.HidD_GetAttributes(h, ref attr))
            {
                info.Vid = attr.VendorID;
                info.Pid = attr.ProductID;
                info.Version = attr.VersionNumber;
            }

            info.Manufacturer = GetHidString(h, Native.HidD_GetManufacturerString);
            info.Product = GetHidString(h, Native.HidD_GetProductString);
            info.Serial = GetHidString(h, Native.HidD_GetSerialNumberString);

            uint? descSize = HidIoctl.GetDescriptorSize(h, out string infoDiag);
            if (descSize.HasValue)
            {
                info.RawReportDescriptor =
                    HidIoctl.GetReportDescriptor(h, descSize.Value, out string descDiag);
                info.DescriptorDiagnostic = $"size={descSize.Value} info={infoDiag} fetch={descDiag}";
            }
            else
            {
                info.DescriptorDiagnostic = infoDiag;
            }

            if (Native.HidD_GetPreparsedData(h, out IntPtr pp) && pp != IntPtr.Zero)
            {
                try
                {
                    var caps = new Native.HIDP_CAPS { Reserved = new ushort[17] };
                    if (Native.HidP_GetCaps(pp, ref caps) == Native.HIDP_STATUS_SUCCESS)
                    {
                        info.Caps = caps;

                        if (caps.NumberInputButtonCaps > 0)
                        {
                            var bc = new Native.HIDP_BUTTON_CAPS[caps.NumberInputButtonCaps];
                            ushort n = caps.NumberInputButtonCaps;
                            if (Native.HidP_GetButtonCaps(Native.HidP_Input, bc, ref n, pp)
                                == Native.HIDP_STATUS_SUCCESS)
                                info.ButtonCaps = bc.Take(n).ToArray();
                        }

                        if (caps.NumberInputValueCaps > 0)
                        {
                            var vc = new Native.HIDP_VALUE_CAPS[caps.NumberInputValueCaps];
                            ushort n = caps.NumberInputValueCaps;
                            if (Native.HidP_GetValueCaps(Native.HidP_Input, vc, ref n, pp)
                                == Native.HIDP_STATUS_SUCCESS)
                                info.ValueCaps = vc.Take(n).ToArray();
                        }

                        if (caps.NumberLinkCollectionNodes > 0)
                        {
                            var lc = new Native.HIDP_LINK_COLLECTION_NODE[caps.NumberLinkCollectionNodes];
                            uint n = caps.NumberLinkCollectionNodes;
                            if (Native.HidP_GetLinkCollectionNodes(lc, ref n, pp)
                                == Native.HIDP_STATUS_SUCCESS)
                                info.LinkCollections = lc.Take((int)n).ToArray();
                        }
                    }
                }
                finally { Native.HidD_FreePreparsedData(pp); }
            }
        }
        finally { Native.CloseHandle(h); }
    }

    private delegate bool HidStringFn(IntPtr device, byte[] buffer, int length);

    private static string GetHidString(IntPtr h, HidStringFn fn)
    {
        var buf = new byte[512];
        if (!fn(h, buf, buf.Length)) return "";
        string s = Encoding.Unicode.GetString(buf);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }

    private static string GetInstanceId(IntPtr set, ref Native.SP_DEVINFO_DATA d)
    {
        Native.SetupDiGetDeviceInstanceId(set, ref d, null, 0, out int req);
        if (req <= 0) return "";
        var buf = new char[req];
        return Native.SetupDiGetDeviceInstanceId(set, ref d, buf, req, out _)
            ? new string(buf).TrimEnd('\0')
            : "";
    }

    private static string GetStringProp(IntPtr set, ref Native.SP_DEVINFO_DATA d, int prop)
    {
        Native.SetupDiGetDeviceRegistryProperty(set, ref d, prop, out _, null, 0, out int req);
        if (req <= 0) return "";
        var buf = new byte[req];
        if (!Native.SetupDiGetDeviceRegistryProperty(set, ref d, prop, out _, buf, req, out _))
            return "";
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    private static string GetMultiStringProp(IntPtr set, ref Native.SP_DEVINFO_DATA d, int prop)
    {
        Native.SetupDiGetDeviceRegistryProperty(set, ref d, prop, out _, null, 0, out int req);
        if (req <= 0) return "";
        var buf = new byte[req];
        if (!Native.SetupDiGetDeviceRegistryProperty(set, ref d, prop, out _, buf, req, out _))
            return "";
        var parts = Encoding.Unicode.GetString(buf)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", parts);
    }
}
