using System.Runtime.InteropServices;

namespace Dovetail.Core;

/// <summary>
/// Retrieves the raw HID report descriptor straight from hidclass.sys.
///
/// Windows exposes no HidD_* call for the raw descriptor bytes, but hidclass.sys answers
/// two IOCTLs that together give it: GET_COLLECTION_INFORMATION reports the descriptor
/// length, GET_COLLECTION_DESCRIPTOR returns the bytes. Codes are built from hidclass.h:
///   HID_CTL_CODE(id)        = CTL_CODE(FILE_DEVICE_KEYBOARD, id, METHOD_NEITHER,  FILE_ANY_ACCESS)
///   HID_BUFFER_CTL_CODE(id) = CTL_CODE(FILE_DEVICE_KEYBOARD, id, METHOD_BUFFERED, FILE_ANY_ACCESS)
/// </summary>
internal static class HidIoctl
{
    private const uint FILE_DEVICE_KEYBOARD = 0x0000000b;
    private const uint METHOD_BUFFERED = 0;
    private const uint METHOD_NEITHER = 3;
    private const uint FILE_ANY_ACCESS = 0;

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    private static readonly uint IOCTL_HID_GET_COLLECTION_INFORMATION =
        CtlCode(FILE_DEVICE_KEYBOARD, 106, METHOD_BUFFERED, FILE_ANY_ACCESS);   // 0x000B01A8

    private static readonly uint IOCTL_HID_GET_COLLECTION_DESCRIPTOR =
        CtlCode(FILE_DEVICE_KEYBOARD, 100, METHOD_NEITHER, FILE_ANY_ACCESS);    // 0x000B0193

    [StructLayout(LayoutKind.Sequential)]
    private struct HID_COLLECTION_INFORMATION
    {
        public uint DescriptorSize;
        public byte Polled;
        public byte Reserved1;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public ushort[] Reserved;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device, uint ioControlCode,
        IntPtr inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize,
        out int bytesReturned, IntPtr overlapped);

    internal static uint IoctlInfoCode => IOCTL_HID_GET_COLLECTION_INFORMATION;
    internal static uint IoctlDescriptorCode => IOCTL_HID_GET_COLLECTION_DESCRIPTOR;

    /// <summary>Descriptor length in bytes, or null when the IOCTL is refused.</summary>
    internal static uint? GetDescriptorSize(IntPtr device, out string diagnostic)
    {
        int size = Marshal.SizeOf<HID_COLLECTION_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
            if (!DeviceIoControl(device, IOCTL_HID_GET_COLLECTION_INFORMATION,
                                 IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero))
            {
                diagnostic = $"IOCTL_HID_GET_COLLECTION_INFORMATION failed, Win32 error {Marshal.GetLastWin32Error()}";
                return null;
            }
            var info = Marshal.PtrToStructure<HID_COLLECTION_INFORMATION>(buf);
            diagnostic = $"ok (VID 0x{info.VendorID:X4} PID 0x{info.ProductID:X4} " +
                         $"ver 0x{info.VersionNumber:X4} polled={info.Polled != 0})";
            return info.DescriptorSize;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Raw report descriptor bytes, or null when the IOCTL is refused.</summary>
    internal static byte[]? GetReportDescriptor(IntPtr device, uint descriptorSize, out string diagnostic)
    {
        if (descriptorSize == 0 || descriptorSize > 64 * 1024)
        {
            diagnostic = $"implausible descriptor size {descriptorSize}";
            return null;
        }

        IntPtr buf = Marshal.AllocHGlobal((int)descriptorSize);
        try
        {
            if (!DeviceIoControl(device, IOCTL_HID_GET_COLLECTION_DESCRIPTOR,
                                 IntPtr.Zero, 0, buf, (int)descriptorSize,
                                 out int returned, IntPtr.Zero))
            {
                diagnostic = $"IOCTL_HID_GET_COLLECTION_DESCRIPTOR failed, Win32 error {Marshal.GetLastWin32Error()}";
                return null;
            }
            int n = returned > 0 ? returned : (int)descriptorSize;
            var bytes = new byte[n];
            Marshal.Copy(buf, bytes, 0, n);
            diagnostic = $"ok ({n} bytes returned)";
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
