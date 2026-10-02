using DxDark.Capture;
using DxDark.Core.Color;

namespace DxDark.Core.Effects;

/// <summary>What the LEDs are sampled from.</summary>
public enum SourceKind
{
    Screen,
    Effect,
}

/// <summary>Which picture to sample, and how to play it.</summary>
/// <param name="EffectId">Built-in effect ID or video effect ID.</param>
/// <param name="VideoPath">Full path of the video file for video effects.</param>
/// <param name="Speed">Playback speed (1 = normal).</param>
/// <param name="Color">Color for the Solid and Pulse effects ("#RRGGBB").</param>
public sealed record SourceSpec(SourceKind Kind, string EffectId, string? VideoPath, double Speed, string Color)
{
    public static SourceSpec ScreenSource { get; } = new(SourceKind.Screen, "", null, 1, "#FFFFFF");

    /// <summary>Identifies the underlying picture; a different key needs a new source object.</summary>
    public string Key => Kind == SourceKind.Screen ? "screen" : $"effect:{EffectId}:{VideoPath}";
}

/// <summary>Supplies frames to the sync engine: the screen, a built-in effect or a video.</summary>
public interface IFrameSource : IDisposable
{
    /// <summary>
    /// Updates <paramref name="frame"/> for time <paramref name="now"/> (seconds). Waits up to
    /// <paramref name="timeoutMs"/> when nothing changes.
    /// </summary>
    CaptureStatus Next(CapturedFrame frame, double now, int timeoutMs, SourceSpec spec);

    string? Label { get; }

    /// <summary>Monitor refresh rate in Hz for the screen; 0 for effects.</summary>
    double RefreshRate { get; }

    string? LastError { get; }
}

/// <summary>The desktop of one monitor.</summary>
public sealed class ScreenSource(string? monitorDeviceName) : IFrameSource
{
    private readonly ScreenCapturer _capturer = new(monitorDeviceName);

    public string? Label => _capturer.Monitor?.Label;

    public double RefreshRate => _capturer.RefreshRate;

    public string? LastError => _capturer.LastError;

    public CaptureStatus Next(CapturedFrame frame, double now, int timeoutMs, SourceSpec spec) =>
        _capturer.TryCapture(frame, timeoutMs);

    public void Dispose() => _capturer.Dispose();
}

/// <summary>A built-in effect drawn in code.</summary>
public sealed class BuiltInEffectSource(BuiltInEffect effect) : IFrameSource
{
    private double _phase = 3; // start a few seconds in, where every effect looks settled
    private double _last = double.NaN;
    private string? _renderedColor;

    public string? Label => effect.Name;

    public double RefreshRate => 0;

    public string? LastError => null;

    public CaptureStatus Next(CapturedFrame frame, double now, int timeoutMs, SourceSpec spec)
    {
        double dt = double.IsNaN(_last) ? 0 : Math.Clamp(now - _last, 0, 0.25);
        _last = now;
        _phase += dt * spec.Speed;

        bool animated = BuiltInEffects.IsAnimated(effect.Id);
        if (!animated && _renderedColor == spec.Color && frame.Width > 0)
        {
            if (timeoutMs > 0)
            {
                Thread.Sleep(timeoutMs); // nothing to update; don't spin
            }

            return CaptureStatus.NoChange;
        }

        ColorMath.TryParseHex(spec.Color, out byte r, out byte g, out byte b);
        BuiltInEffects.Render(effect.Id, frame, _phase, r, g, b);
        _renderedColor = spec.Color;
        return CaptureStatus.NewFrame;
    }

    public void Dispose()
    {
    }
}

/// <summary>A looping video file.</summary>
public sealed class VideoSource : IFrameSource
{
    private readonly VideoReader? _reader;
    private readonly CapturedFrame _next = new();
    private double _nextTime = double.NaN;
    private double _position;
    private double _lastFrameTime;
    private double _frameInterval = 1 / 30.0;
    private double _last = double.NaN;

    public VideoSource(string path, string name)
    {
        Label = name;
        try
        {
            _reader = new VideoReader(path);
        }
        catch (Exception ex)
        {
            LastError = $"The video \"{name}\" could not be opened: {ex.Message}";
            Log.Warn(LastError);
        }
    }

    public string? Label { get; }

    public double RefreshRate => 0;

    public string? LastError { get; private set; }

    public CaptureStatus Next(CapturedFrame frame, double now, int timeoutMs, SourceSpec spec)
    {
        if (_reader is null)
        {
            Thread.Sleep(Math.Max(timeoutMs, 100));
            return CaptureStatus.Unavailable;
        }

        double dt = double.IsNaN(_last) ? 0 : Math.Clamp(now - _last, 0, 0.25);
        _last = now;
        _position += dt * spec.Speed;

        try
        {
            if (double.IsNaN(_nextTime) && !Decode())
            {
                return CaptureStatus.Unavailable;
            }

            bool shown = false;
            for (int i = 0; i < 12 && _nextTime <= _position; i++)
            {
                _next.CopyTo(frame);
                shown = true;
                if (!Decode())
                {
                    return CaptureStatus.Unavailable;
                }
            }

            if (_nextTime - _position > 2)
            {
                _position = _nextTime; // fell far behind (e.g. very high speed): resynchronise
            }

            if (!shown && timeoutMs > 0)
            {
                double wait = (_nextTime - _position) / Math.Max(0.05, spec.Speed) * 1000;
                Thread.Sleep((int)Math.Clamp(wait, 1, timeoutMs));
            }

            return shown ? CaptureStatus.NewFrame : CaptureStatus.NoChange;
        }
        catch (Exception ex)
        {
            LastError = $"Playing \"{Label}\" failed: {ex.Message}";
            Log.Warn(LastError);
            return CaptureStatus.Unavailable;
        }
    }

    /// <summary>Decodes the next frame into the look-ahead buffer, looping at the end.</summary>
    private bool Decode()
    {
        if (_reader!.ReadFrame(_next, out double time))
        {
            if (!double.IsNaN(_nextTime) && time > _nextTime)
            {
                _frameInterval = Math.Clamp(time - _nextTime, 1 / 240.0, 1);
            }

            _nextTime = time;
            _lastFrameTime = time;
            return true;
        }

        // End of the video: start again and keep the playback clock in step.
        double duration = _lastFrameTime + _frameInterval;
        _reader.Rewind();
        if (!_reader.ReadFrame(_next, out double first))
        {
            LastError = $"\"{Label}\" contains no frames that can be played.";
            return false;
        }

        _position = Math.Max(0, _position - duration);
        _nextTime = first;
        _lastFrameTime = first;
        return true;
    }

    public void Dispose() => _reader?.Dispose();
}
