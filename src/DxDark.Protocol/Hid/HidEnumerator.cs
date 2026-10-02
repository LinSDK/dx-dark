using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static DxDark.Protocol.Hid.NativeMethods;

namespace DxDark.Protocol.Hid;

/// <summary>A HID collection found on the system.</summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    string? Manufacturer,
    string? Product);

/// <summary>Lists HID collections through SetupAPI.</summary>
public static unsafe class HidEnumerator
{
    public static List<HidDeviceInfo> Enumerate(ushort vendorId, ushort productId)
    {
        var result = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandleValue)
        {
            return result;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    break;
                }

                string? path = GetDevicePath(set, ref interfaceData);
                if (path is not null && Query(path, vendorId, productId) is { } info)
                {
                    result.Add(info);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string? GetDevicePath(IntPtr set, ref SpDeviceInterfaceData interfaceData)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out int required, IntPtr.Zero);
        if (required <= 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: size of the fixed part, 8 on 64-bit Windows.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, required, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads a collection's IDs and capabilities without requesting read/write access.</summary>
    private static HidDeviceInfo? Query(string path, ushort vendorId, ushort productId)
    {
        using SafeFileHandle handle = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes)
            || attributes.VendorId != vendorId
            || attributes.ProductId != productId)
        {
            return null;
        }

        if (!TryGetCaps(handle, out HidpCaps caps))
        {
            return null;
        }

        return new HidDeviceInfo(
            path,
            attributes.VendorId,
            attributes.ProductId,
            caps.UsagePage,
            caps.Usage,
            caps.InputReportByteLength,
            caps.OutputReportByteLength,
            ReadString(handle, manufacturer: true),
            ReadString(handle, manufacturer: false));
    }

    internal static bool TryGetCaps(SafeFileHandle handle, out HidpCaps caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            return false;
        }

        try
        {
            return HidP_GetCaps(preparsed, out caps) == HidpStatusSuccess;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static string? ReadString(SafeFileHandle handle, bool manufacturer)
    {
        const int chars = 128;
        char* buffer = stackalloc char[chars];
        bool ok = manufacturer
            ? HidD_GetManufacturerString(handle, buffer, chars * sizeof(char))
            : HidD_GetProductString(handle, buffer, chars * sizeof(char));
        if (!ok)
        {
            return null;
        }

        buffer[chars - 1] = '\0';
        return new string(buffer).Trim();
    }
}
