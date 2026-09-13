using System.Runtime.InteropServices;

namespace Dovetail.Core;

/// <summary>
/// P/Invoke surface for SetupAPI, hid.dll and the pieces of kernel32 needed to read raw
/// HID input reports. Layouts follow hidpi.h exactly; sizes are asserted at startup.
/// </summary>
public static class Native
{
    // ---- SetupAPI ----------------------------------------------------------------

    internal const int DIGCF_PRESENT = 0x02;
    internal const int DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    // SPDRP_* device registry properties
    internal const int SPDRP_DEVICEDESC = 0x00;
    internal const int SPDRP_HARDWAREID = 0x01;
    internal const int SPDRP_SERVICE = 0x04;
    internal const int SPDRP_CLASS = 0x07;
    internal const int SPDRP_FRIENDLYNAME = 0x0C;
    internal const int SPDRP_LOCATION_INFORMATION = 0x0D;
    internal const int SPDRP_UPPERFILTERS = 0x11;
    internal const int SPDRP_LOWERFILTERS = 0x12;

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
        out int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, int property,
        out int propertyRegDataType, byte[]? propertyBuffer, int propertyBufferSize,
        out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        char[]? deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ---- kernel32 ----------------------------------------------------------------

    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    internal const uint INFINITE = 0xFFFFFFFF;
    internal const int ERROR_IO_PENDING = 997;
    internal const uint WAIT_TIMEOUT = 258;
    internal const uint WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateEvent(
        IntPtr securityAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(
        IntPtr handle, byte[] buffer, int numberOfBytesToRead,
        IntPtr numberOfBytesRead, IntPtr overlapped);

    /// <summary>
    /// Overload taking a raw buffer address. Required for overlapped reads: the driver
    /// writes into the buffer after ReadFile returns, so the memory must stay pinned for
    /// the lifetime of the operation, which the byte[] marshaller does not guarantee.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
    internal static extern bool ReadFilePtr(
        IntPtr handle, IntPtr buffer, int numberOfBytesToRead,
        IntPtr numberOfBytesRead, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(
        IntPtr handle, IntPtr overlapped, out int numberOfBytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIo(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    // ---- hid.dll -----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    /// <summary>hidpi.h HIDP_CAPS. 17 reserved words between the report lengths and the counts.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>hidpi.h HIDP_BUTTON_CAPS, 72 bytes. The trailing union is flattened to its Range arm.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public uint[] Reserved;
        // union { Range | NotRange } - both arms are eight USHORTs
        public ushort UsageMin;      // NotRange.Usage when IsRange == 0
        public ushort UsageMax;      // NotRange.Reserved1
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;  // NotRange.DataIndex when IsRange == 0
        public ushort DataIndexMax;
    }

    /// <summary>hidpi.h HIDP_VALUE_CAPS, 72 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;
        // union { Range | NotRange }
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_LINK_COLLECTION_NODE
    {
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public ushort Parent;
        public ushort NumberOfChildren;
        public ushort NextSibling;
        public ushort FirstChild;
        public uint Bitfield;   // CollectionType:8, IsAlias:1, Reserved:23
        public IntPtr UserContext;

        public byte CollectionType => (byte)(Bitfield & 0xFF);
        public bool IsAlias => ((Bitfield >> 8) & 0x1) != 0;
    }

    internal const int HidP_Input = 0;
    internal const int HidP_Output = 1;
    internal const int HidP_Feature = 2;

    internal const int HIDP_STATUS_SUCCESS = 0x00110000;

    [DllImport("hid.dll")]
    internal static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool HidD_GetManufacturerString(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool HidD_GetSerialNumberString(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_SetNumInputBuffers(IntPtr device, int numberBuffers);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetNumInputBuffers(IntPtr device, out int numberBuffers);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetButtonCaps(
        int reportType, [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(
        int reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength,
        IntPtr preparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetLinkCollectionNodes(
        [Out] HIDP_LINK_COLLECTION_NODE[] linkCollectionNodes,
        ref uint linkCollectionNodesLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsages(
        int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usageList, ref uint usageLength, IntPtr preparsedData,
        byte[] report, int reportLength);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint usageValue, IntPtr preparsedData, byte[] report, int reportLength);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetScaledUsageValue(
        int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out int usageValue, IntPtr preparsedData, byte[] report, int reportLength);
}
