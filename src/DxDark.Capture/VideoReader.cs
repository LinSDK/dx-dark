using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DxDark.Capture;

/// <summary>
/// Decodes a video file frame by frame with Windows Media Foundation (MP4, MOV, WMV, AVI, MKV…,
/// whatever codecs Windows has). Windows converts and shrinks the frames to about
/// <see cref="DefaultWidth"/> pixels wide, which is all the LED sampling needs. Use from one thread.
/// </summary>
public sealed unsafe class VideoReader : IDisposable
{
    public const int DefaultWidth = 320;

    private readonly IMFSourceReader _reader;
    private readonly bool _uninitializeCom;
    private int _stride;
    private bool _disposed;

    public VideoReader(string path, int maxWidth = DefaultWidth)
    {
        // Media Foundation needs COM on the calling thread.
        int hr = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        _uninitializeCom = hr is 0 or 1;
        try
        {
            MediaFactory.MFStartup(useLightVersion: true).CheckError();
            using IMFAttributes attributes = MediaFactory.MFCreateAttributes(1);
            attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
            _reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
            _reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            _reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            using (IMFMediaType native = _reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                (SourceWidth, SourceHeight) = Unpack(native.GetUInt64(MediaTypeAttributeKeys.FrameSize));
            }

            ConfigureOutput(maxWidth);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Size of the video file's frames.</summary>
    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Size of the decoded (shrunken) frames.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Decodes the next frame into <paramref name="frame"/>. Returns false at the end of the
    /// video (call <see cref="Rewind"/> to loop).
    /// </summary>
    public bool ReadFrame(CapturedFrame frame, out double timestampSeconds)
    {
        timestampSeconds = 0;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            IMFSample? sample = _reader.ReadSample(
                SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                out _, out SourceReaderFlag flags, out long timestamp);
            using (sample)
            {
                if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
                {
                    return false;
                }

                if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                {
                    ReadOutputFormat();
                }

                if (sample is null)
                {
                    continue; // a gap in the stream
                }

                using IMFMediaBuffer buffer = sample.ConvertToContiguousBuffer();
                buffer.Lock(out IntPtr data, out _, out int length);
                try
                {
                    CopyPixels((byte*)data, length, frame);
                }
                finally
                {
                    buffer.Unlock();
                }

                timestampSeconds = timestamp / 10_000_000.0;
                return true;
            }
        }

        return false;
    }

    /// <summary>Goes back to the start of the video.</summary>
    public void Rewind() => _reader.SetCurrentPosition(0);

    private void ConfigureOutput(int maxWidth)
    {
        int width = Math.Min(SourceWidth, maxWidth) & ~1;
        int height = Math.Max(2, (int)Math.Round(SourceHeight * (width / (double)Math.Max(1, SourceWidth)))) & ~1;
        try
        {
            // Ask Windows to convert to 32-bit RGB and shrink the frames while decoding.
            SetOutput(width, height);
        }
        catch (SharpGenException)
        {
            // Resizing not available for this file: decode at full size and shrink here instead.
            SetOutput(0, 0);
        }

        ReadOutputFormat();
    }

    private void SetOutput(int width, int height)
    {
        using IMFMediaType type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
        if (width > 0 && height > 0)
        {
            type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)width << 32) | (uint)height);
        }

        _reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
    }

    private void ReadOutputFormat()
    {
        using IMFMediaType current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        (Width, Height) = Unpack(current.GetUInt64(MediaTypeAttributeKeys.FrameSize));
        _stride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint stride).Success
            ? unchecked((int)stride)
            : Width * 4;
    }

    /// <summary>Copies (and if needed shrinks) a decoded RGB32 frame into a top-down BGRA frame.</summary>
    private void CopyPixels(byte* source, int length, CapturedFrame frame)
    {
        int stride = _stride == 0 ? Width * 4 : _stride;
        byte* firstRow = stride < 0 ? source + (Height - 1) * (long)(-stride) : source;
        if ((long)Math.Abs(stride) * Height > length)
        {
            return; // unexpected buffer size: skip the frame
        }

        // Shrink by whole-pixel blocks when Windows delivered a larger frame than needed.
        int factor = Math.Max(1, (int)Math.Ceiling(Width / (double)DefaultWidth));
        int outWidth = Math.Max(1, Width / factor);
        int outHeight = Math.Max(1, Height / factor);
        frame.EnsureSize(outWidth, outHeight);
        frame.SourceWidth = SourceWidth;
        frame.SourceHeight = SourceHeight;

        fixed (byte* destination = frame.Pixels)
        {
            if (factor == 1)
            {
                for (int y = 0; y < outHeight; y++)
                {
                    uint* src = (uint*)(firstRow + (long)y * stride);
                    uint* dst = (uint*)(destination + y * outWidth * 4);
                    for (int x = 0; x < outWidth; x++)
                    {
                        dst[x] = src[x] | 0xFF000000; // RGB32 leaves the alpha byte undefined
                    }
                }
            }
            else
            {
                int area = factor * factor;
                for (int y = 0; y < outHeight; y++)
                {
                    byte* dst = destination + y * outWidth * 4;
                    for (int x = 0; x < outWidth; x++)
                    {
                        int b = 0, g = 0, r = 0;
                        for (int dy = 0; dy < factor; dy++)
                        {
                            byte* src = firstRow + (long)(y * factor + dy) * stride + x * factor * 4;
                            for (int dx = 0; dx < factor; dx++, src += 4)
                            {
                                b += src[0];
                                g += src[1];
                                r += src[2];
                            }
                        }

                        dst[x * 4] = (byte)(b / area);
                        dst[x * 4 + 1] = (byte)(g / area);
                        dst[x * 4 + 2] = (byte)(r / area);
                        dst[x * 4 + 3] = 255;
                    }
                }
            }
        }

        frame.Sequence++;
    }

    private static (int, int) Unpack(ulong size) => ((int)(size >> 32), (int)(size & 0xFFFFFFFF));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader?.Dispose();
        MediaFactory.MFShutdown();
        if (_uninitializeCom)
        {
            CoUninitialize();
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
