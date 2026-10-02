using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DxDark.Capture;

public enum CaptureStatus
{
    /// <summary>The frame buffer holds a new desktop image.</summary>
    NewFrame,

    /// <summary>Nothing on screen changed within the timeout.</summary>
    NoChange,

    /// <summary>Capture is temporarily unavailable (mode change, lock screen, UAC prompt…); retry later.</summary>
    Unavailable,
}

/// <summary>
/// Captures one monitor with the Desktop Duplication API. Each frame is copied into a mip-mapped
/// texture, the GPU averages it down to roughly 320–480 pixels wide, and only that small image
/// is read back. The mouse cursor is never included.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private const int TargetAnalysisWidth = 320;

    // DXGI result codes.
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private static readonly FeatureLevel[] FeatureLevels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private readonly string? _requestedDeviceName;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private ID3D11Texture2D? _staging;
    private uint _mipLevel;
    private int _desktopWidth;
    private int _desktopHeight;
    private int _analysisWidth;
    private int _analysisHeight;
    private DisplayRotation _rotation;
    private long _sequence;

    /// <param name="deviceName">Monitor to capture (e.g. <c>\\.\DISPLAY2</c>); null or unknown means the primary monitor.</param>
    public ScreenCapturer(string? deviceName)
    {
        _requestedDeviceName = deviceName;
    }

    /// <summary>The monitor being captured, once capture has started.</summary>
    public MonitorInfo? Monitor { get; private set; }

    /// <summary>Refresh rate of the captured monitor in Hz (0 if unknown). Capture adapts to any rate.</summary>
    public double RefreshRate { get; private set; }

    /// <summary>Last error, for diagnostics.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Grabs the latest desktop image if it changed. Waits up to <paramref name="timeoutMs"/>
    /// for a change (0 returns immediately).
    /// </summary>
    public CaptureStatus TryCapture(CapturedFrame frame, int timeoutMs)
    {
        if (_duplication is null && !TryInitialize())
        {
            return CaptureStatus.Unavailable;
        }

        Result result = _duplication!.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out OutduplFrameInfo info, out IDXGIResource? resource);
        if (result.Code == DxgiErrorWaitTimeout)
        {
            return CaptureStatus.NoChange;
        }

        if (result.Failure)
        {
            LastError = result.Code == DxgiErrorAccessLost
                ? "Capture was interrupted (display mode change, lock screen or full-screen switch)."
                : $"AcquireNextFrame failed (0x{result.Code:X8}).";
            ReleaseDuplication();
            return CaptureStatus.Unavailable;
        }

        try
        {
            if (info.LastPresentTime == 0)
            {
                // Only the mouse pointer moved.
                return CaptureStatus.NoChange;
            }

            using (ID3D11Texture2D desktop = resource!.QueryInterface<ID3D11Texture2D>())
            {
                _context!.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, desktop, 0);
            }

            _context.GenerateMips(_mipView!);
            _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _mipTexture!, _mipLevel);
            ReadStaging(frame);
            return CaptureStatus.NewFrame;
        }
        catch (SharpGenException ex)
        {
            LastError = $"Capture failed: {ex.Message}";
            resource?.Dispose();
            resource = null;
            ReleaseDuplication();
            return CaptureStatus.Unavailable;
        }
        finally
        {
            // Released only after the GPU work above has been read back, so the desktop surface is
            // never handed back to Windows while a copy from it is still queued.
            resource?.Dispose();
            _duplication?.ReleaseFrame();
        }
    }

    private unsafe void ReadStaging(CapturedFrame frame)
    {
        MappedSubresource mapped = _context!.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            bool swap = _rotation is DisplayRotation.Rotate90 or DisplayRotation.Rotate270;
            int w0 = _analysisWidth;
            int h0 = _analysisHeight;
            frame.EnsureSize(swap ? h0 : w0, swap ? w0 : h0);
            frame.SourceWidth = swap ? _desktopHeight : _desktopWidth;
            frame.SourceHeight = swap ? _desktopWidth : _desktopHeight;

            byte* src = (byte*)mapped.DataPointer;
            int pitch = (int)mapped.RowPitch;
            fixed (byte* dstBase = frame.Pixels)
            {
                if (_rotation == DisplayRotation.Identity)
                {
                    for (int y = 0; y < h0; y++)
                    {
                        Buffer.MemoryCopy(src + y * pitch, dstBase + y * w0 * 4, w0 * 4, w0 * 4);
                    }
                }
                else
                {
                    // The duplicated image is in the panel's native orientation; rotate it
                    // clockwise by the display rotation to match what is shown on screen.
                    uint* dst = (uint*)dstBase;
                    int dw = frame.Width;
                    int dh = frame.Height;
                    for (int y = 0; y < dh; y++)
                    {
                        for (int x = 0; x < dw; x++)
                        {
                            (int sx, int sy) = _rotation switch
                            {
                                DisplayRotation.Rotate90 => (y, h0 - 1 - x),
                                DisplayRotation.Rotate180 => (w0 - 1 - x, h0 - 1 - y),
                                _ => (w0 - 1 - y, x),
                            };
                            dst[y * dw + x] = *(uint*)(src + sy * pitch + sx * 4);
                        }
                    }
                }
            }

            frame.Sequence = ++_sequence;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private bool TryInitialize()
    {
        try
        {
            if (_device is null)
            {
                CreateDevice();
            }

            IDXGIOutputDuplication duplication = _output!.DuplicateOutput(_device!)
                ?? throw new InvalidOperationException("DuplicateOutput returned no object.");
            _duplication = duplication;
            OutduplDescription desc = duplication.Description;
            _rotation = MonitorEnumerator.ToRotation(desc.Rotation);
            var refresh = desc.ModeDescription.RefreshRate;
            RefreshRate = refresh.Denominator > 0 ? refresh.Numerator / (double)refresh.Denominator : 0;
            int width = (int)desc.ModeDescription.Width;
            int height = (int)desc.ModeDescription.Height;
            if (width != _desktopWidth || height != _desktopHeight || _mipTexture is null)
            {
                CreateTextures(width, height);
            }

            LastError = null;
            return true;
        }
        catch (SharpGenException ex)
        {
            // Typical: E_ACCESSDENIED on the lock screen / UAC prompt, DXGI_ERROR_UNSUPPORTED on
            // some hybrid-GPU laptops, DXGI_ERROR_NOT_CURRENTLY_AVAILABLE when too many apps capture.
            LastError = $"Screen capture unavailable (0x{ex.ResultCode.Code:X8}).";
            ReleaseDuplication();
            if (ex.ResultCode.Code is unchecked((int)0x887A0005) or unchecked((int)0x887A0007))
            {
                ReleaseDevice(); // device removed/reset: start over next time
            }

            return false;
        }
    }

    private void CreateDevice()
    {
        (IDXGIAdapter1 adapter, IDXGIOutput1 output, MonitorInfo monitor) = FindOutput(_requestedDeviceName);
        _adapter = adapter;
        _output = output;
        Monitor = monitor;

        // The device must live on the adapter that drives the monitor, or duplication fails.
        D3D11.D3D11CreateDevice(
            _adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            FeatureLevels, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device;
        _context = context;
    }

    private void CreateTextures(int width, int height)
    {
        ReleaseTextures();
        _desktopWidth = width;
        _desktopHeight = height;

        int longSide = Math.Max(width, height);
        uint level = 0;
        while ((longSide >> (int)(level + 1)) >= TargetAnalysisWidth)
        {
            level++;
        }

        _mipLevel = level;
        _analysisWidth = Math.Max(1, width >> (int)level);
        _analysisHeight = Math.Max(1, height >> (int)level);

        // sRGB format so the GPU averages in linear light; fall back to plain UNorm if needed.
        foreach (Format format in new[] { Format.B8G8R8A8_UNorm_SRgb, Format.B8G8R8A8_UNorm })
        {
            try
            {
                _mipTexture = _device!.CreateTexture2D(
                    format, (uint)width, (uint)height, arraySize: 1, mipLevels: level + 1, initialData: null,
                    bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget,
                    miscFlags: ResourceOptionFlags.GenerateMips);
                _mipView = _device.CreateShaderResourceView(_mipTexture);
                _staging = _device.CreateTexture2D(
                    format, (uint)_analysisWidth, (uint)_analysisHeight, arraySize: 1, mipLevels: 1, initialData: null,
                    bindFlags: BindFlags.None, usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read);
                return;
            }
            catch (SharpGenException) when (format == Format.B8G8R8A8_UNorm_SRgb)
            {
                ReleaseTextures();
            }
        }
    }

    /// <summary>Finds the requested monitor, else the primary one, else the first attached one.</summary>
    private static (IDXGIAdapter1, IDXGIOutput1, MonitorInfo) FindOutput(string? deviceName)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        // Pass 1: pick the adapter/output indices.
        (uint Adapter, uint Output, MonitorInfo Info)? match = null, primary = null, first = null;
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success; o++)
                {
                    using (output)
                    {
                        OutputDescription d = output.Description;
                        if (!d.AttachedToDesktop)
                        {
                            continue;
                        }

                        var b = d.DesktopCoordinates;
                        var info = new MonitorInfo(
                            d.DeviceName, "", b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top,
                            b.Left == 0 && b.Top == 0, MonitorEnumerator.ToRotation(d.Rotation), adapter.Description1.Description);
                        if (match is null && deviceName is not null && string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            match = (a, o, info);
                        }

                        if (primary is null && info.IsPrimary)
                        {
                            primary = (a, o, info);
                        }

                        first ??= (a, o, info);
                    }
                }
            }
        }

        var chosen = match ?? primary ?? first ?? throw new InvalidOperationException("No monitor is attached to the desktop.");

        // Pass 2: open just that adapter and output.
        factory.EnumAdapters1(chosen.Adapter, out IDXGIAdapter1? chosenAdapter).CheckError();
        chosenAdapter.EnumOutputs(chosen.Output, out IDXGIOutput? chosenOutput).CheckError();
        using (chosenOutput)
        {
            return (chosenAdapter, chosenOutput.QueryInterface<IDXGIOutput1>(), chosen.Info);
        }
    }

    private void ReleaseDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    private void ReleaseTextures()
    {
        _staging?.Dispose();
        _mipView?.Dispose();
        _mipTexture?.Dispose();
        _staging = null;
        _mipView = null;
        _mipTexture = null;
    }

    private void ReleaseDevice()
    {
        ReleaseTextures();
        _context?.Dispose();
        _device?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
        _context = null;
        _device = null;
        _output = null;
        _adapter = null;
        _desktopWidth = 0;
        _desktopHeight = 0;
    }

    public void Dispose()
    {
        ReleaseDuplication();
        ReleaseDevice();
    }
}
