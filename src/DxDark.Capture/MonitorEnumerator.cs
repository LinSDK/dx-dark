using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace DxDark.Capture;

/// <summary>Lists monitors through DXGI, with friendly names from the Windows display configuration.</summary>
public static class MonitorEnumerator
{
    public static List<MonitorInfo> List()
    {
        var monitors = new List<MonitorInfo>();
        Dictionary<string, string> friendlyNames = DisplayNames.Read();

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                string adapterName = adapter.Description1.Description;
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        OutputDescription d = output.Description;
                        if (!d.AttachedToDesktop)
                        {
                            continue;
                        }

                        var bounds = d.DesktopCoordinates;
                        monitors.Add(new MonitorInfo(
                            d.DeviceName,
                            friendlyNames.GetValueOrDefault(d.DeviceName, ""),
                            bounds.Left,
                            bounds.Top,
                            bounds.Right - bounds.Left,
                            bounds.Bottom - bounds.Top,
                            IsPrimary: bounds.Left == 0 && bounds.Top == 0,
                            ToRotation(d.Rotation),
                            adapterName));
                    }
                }
            }
        }

        return monitors;
    }

    internal static DisplayRotation ToRotation(ModeRotation rotation) => rotation switch
    {
        ModeRotation.Rotate90 => DisplayRotation.Rotate90,
        ModeRotation.Rotate180 => DisplayRotation.Rotate180,
        ModeRotation.Rotate270 => DisplayRotation.Rotate270,
        _ => DisplayRotation.Identity,
    };

    /// <summary>Maps GDI device names (\\.\DISPLAY1) to monitor model names via QueryDisplayConfig.</summary>
    private static class DisplayNames
    {
        private const uint QdcOnlyActivePaths = 0x2;
        private const uint GetSourceName = 1;
        private const uint GetTargetName = 2;

        public static Dictionary<string, string> Read()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
                {
                    return names;
                }

                var paths = new PathInfo[pathCount];
                var modes = new ModeInfo[modeCount];
                if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                {
                    return names;
                }

                for (int i = 0; i < pathCount; i++)
                {
                    var source = new SourceDeviceName
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = GetSourceName,
                            Size = (uint)Marshal.SizeOf<SourceDeviceName>(),
                            AdapterId = paths[i].SourceAdapterId,
                            Id = paths[i].SourceId,
                        },
                    };
                    var target = new TargetDeviceName
                    {
                        Header = new DeviceInfoHeader
                        {
                            Type = GetTargetName,
                            Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
                            AdapterId = paths[i].TargetAdapterId,
                            Id = paths[i].TargetId,
                        },
                    };

                    if (DisplayConfigGetDeviceInfo(ref source) == 0
                        && DisplayConfigGetDeviceInfo(ref target) == 0
                        && !string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName))
                    {
                        names[source.ViewGdiDeviceName] = target.MonitorFriendlyDeviceName;
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                // Friendly names are cosmetic; fall back to "Display N".
            }

            return names;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        /// <summary>DISPLAYCONFIG_PATH_INFO (72 bytes), flattened.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PathInfo
        {
            public Luid SourceAdapterId;
            public uint SourceId;
            public uint SourceModeInfoIdx;
            public uint SourceStatusFlags;
            public Luid TargetAdapterId;
            public uint TargetId;
            public uint TargetModeInfoIdx;
            public uint OutputTechnology;
            public uint Rotation;
            public uint Scaling;
            public uint RefreshNumerator;
            public uint RefreshDenominator;
            public uint ScanLineOrdering;
            public int TargetAvailable;
            public uint TargetStatusFlags;
            public uint Flags;
        }

        /// <summary>DISPLAYCONFIG_MODE_INFO (64 bytes); the 48-byte union is not needed.</summary>
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct ModeInfo
        {
            public uint InfoType;
            public uint Id;
            public Luid AdapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoHeader
        {
            public uint Type;
            public uint Size;
            public Luid AdapterId;
            public uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SourceDeviceName
        {
            public DeviceInfoHeader Header;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string ViewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TargetDeviceName
        {
            public DeviceInfoHeader Header;
            public uint Flags;
            public uint OutputTechnology;
            public ushort EdidManufactureId;
            public ushort EdidProductCodeId;
            public uint ConnectorInstance;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string MonitorFriendlyDeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string MonitorDevicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags, ref uint numPathArrayElements, [Out] PathInfo[] pathArray,
            ref uint numModeInfoArrayElements, [Out] ModeInfo[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
    }
}
