using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using DxDark.Capture;
using DxDark.Core.Effects;
using DxDark.Core.Imaging;
using DxDark.Core.Layout;
using DxDark.Core.Platform;
using DxDark.Core.Profiles;
using DxDark.Core.Settings;
using DxDark.Protocol;

namespace DxDark.Core.Sync;

/// <summary>Immutable settings snapshot the engine works from; replace it to apply changes.</summary>
/// <param name="Calibrating">The calibration screen is open: sample the screen, and only near the edges.</param>
public sealed record SyncConfig(
    Profile Profile,
    LedLayout Layout,
    CalibrationSettings Calibration,
    int StripLedCount,
    string? MonitorDeviceName,
    SourceSpec Source,
    bool Calibrating = false);

/// <summary>
/// The sync loop on its own thread: picture (screen, effect or video) → analysis → color pipeline
/// → strip. Sends only when the colors change (plus a keep-alive each second) and idles cheaply
/// while nothing moves. Runs without sending when only the live view needs it.
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private const double KeepAliveSeconds = 1.0;
    private const double PreviewInterval = 1.0 / 30;
    private const int SnapshotPoolSize = 3;

    /// <summary>While calibrating, zones stay within this share of the screen so the controls in the middle are never sampled.</summary>
    private const double CalibrationMaxDepth = 0.15;

    /// <summary>When black bars appear or go, the LEDs fade from the old picture area to the new one over this time.</summary>
    private const double BarFadeSeconds = 0.7;

    private static readonly int[][] ChannelOrders =
    [
        [0, 1, 2], // RGB
        [0, 2, 1], // RBG
        [1, 0, 2], // GRB
        [1, 2, 0], // GBR
        [2, 0, 1], // BRG
        [2, 1, 0], // BGR
    ];

    private readonly Func<LightStrip?> _stripProvider;
    private readonly object _sendGate = new();
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<PreviewSnapshot> _freeSnapshots = new();
    private volatile SyncConfig _config;
    private volatile bool _stop;
    private volatile bool _sendEnabled;
    private volatile bool _previewEnabled;
    private volatile bool _paused;
    private volatile bool _forceSend = true;
    private volatile SyncStats _stats = SyncStats.Idle;
    private ZoneChase? _chase; // read and written with Volatile / Interlocked

    public SyncEngine(Func<LightStrip?> stripProvider, SyncConfig config)
    {
        _stripProvider = stripProvider;
        _config = config;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sync engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Raised on the engine thread about 30 times a second while the preview is enabled.</summary>
    public event Action<PreviewSnapshot>? PreviewReady;

    public SyncStats Stats => _stats;

    /// <summary>True while a zone is being shown on the strip.</summary>
    public bool IsShowingZone => Volatile.Read(ref _chase) is not null;

    /// <summary>Send frames to the strip.</summary>
    public bool SendEnabled
    {
        get => _sendEnabled;
        set
        {
            if (value && !_sendEnabled)
            {
                _forceSend = true;
            }

            _sendEnabled = value;
            _wake.Set();
        }
    }

    /// <summary>Run and publish previews even when not sending.</summary>
    public bool PreviewEnabled
    {
        get => _previewEnabled;
        set
        {
            if (value && !_previewEnabled)
            {
                _freeSnapshots.Clear();
                for (int i = 0; i < SnapshotPoolSize; i++)
                {
                    _freeSnapshots.Enqueue(new PreviewSnapshot());
                }
            }

            _previewEnabled = value;
            _wake.Set();
        }
    }

    /// <summary>Temporarily stop sending (during the speed test) without stopping the loop.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (!value && _paused)
            {
                _forceSend = true;
            }

            _paused = value;
            _wake.Set();
        }
    }

    public void Configure(SyncConfig config)
    {
        _config = config;
        _forceSend = true;
        _wake.Set();
    }

    /// <summary>Send the next frame even if it is unchanged (e.g. after reconnecting).</summary>
    public void Invalidate()
    {
        _forceSend = true;
        _wake.Set();
    }

    /// <summary>
    /// Shows LEDs [<paramref name="firstLed"/>, +<paramref name="ledCount"/>) on the strip instead of
    /// the picture until <see cref="StopShowingZone"/> (see <see cref="ZoneChase"/>). Replaces any zone being shown.
    /// </summary>
    public void ShowZone(int firstLed, int ledCount, double dotDelaySeconds = 0)
    {
        Volatile.Write(ref _chase, new ZoneChase(firstLed, ledCount, dotDelaySeconds));
        _forceSend = true;
        _wake.Set();
    }

    public void StopShowingZone()
    {
        if (Interlocked.Exchange(ref _chase, null) is not null)
        {
            _forceSend = true;
            _wake.Set();
        }
    }

    public void Recycle(PreviewSnapshot snapshot) => _freeSnapshots.Enqueue(snapshot);

    /// <summary>
    /// Waits for a frame that is being sent right now. After setting <see cref="SendEnabled"/> to
    /// false or <see cref="Paused"/> to true, call this before sending other commands so a late
    /// sync frame cannot overwrite them.
    /// </summary>
    public void WaitForPendingSend()
    {
        lock (_sendGate)
        {
        }
    }

    private void Run()
    {
        using var sleeper = new PrecisionSleeper();
        var frame = new CapturedFrame();
        var filtered = new CapturedFrame();
        var filters = new FrameFilters();
        var analyzer = new FrameAnalyzer();
        var bars = new BlackBarDetector();
        var pipeline = new ColorPipeline();
        var clock = Stopwatch.StartNew();

        IFrameSource? source = null;
        string? sourceKey = null;
        SyncConfig? geometryFor = null;
        LedPlacement[] placements = [];
        int[] physicalToLayout = [];
        int[] zoneBounds = [0];
        bool closedLoop = false;
        Vector3[] samples = [];
        PixelRect[] rects = [];
        PixelRect[] fadeRects = [];
        PixelRect content = default;
        PixelRect fadeFrom = default;
        double fadeStart = double.NegativeInfinity;
        SyncConfig? filteredFor = null;
        long filteredSequence = 0;
        CapturedFrame picture = frame;
        bool haveFrame = false;
        bool rectsDirty = true;
        byte[] lastSent = [];
        byte[] chaseOutput = [];
        LedRange[] ranges = [];
        PreviewLut? lut = null;
        SyncConfig? lutFor = null;
        int lutVersion = 0;
        double nextDeadline = 0;
        double lastProcess = 0, lastSend = double.NegativeInfinity, lastPreview = double.NegativeInfinity;
        double idleSince = double.NaN;
        double statsStart = 0, processTime = 0, lastStatsLog = -50;
        int captured = 0, sent = 0, processed = 0;

        try
        {
            while (!_stop)
            {
                ZoneChase? chase = Volatile.Read(ref _chase);

                SyncConfig cfg = _config;
                LightStrip? strip = _sendEnabled ? _stripProvider() : null;
                bool sending = _sendEnabled && !_paused && strip is { IsConnected: true };
                bool active = sending || _previewEnabled;
                double loopStart = clock.Elapsed.TotalSeconds;

                if (!active)
                {
                    // Release the GPU capture or video decoder after a few idle seconds.
                    if (double.IsNaN(idleSince))
                    {
                        idleSince = loopStart;
                    }
                    else if (source is not null && loopStart - idleSince > 5)
                    {
                        source.Dispose();
                        source = null;
                        sourceKey = null;
                        haveFrame = false;
                    }

                    _stats = SyncStats.Idle;
                    _wake.WaitOne(500);
                    continue;
                }

                idleSince = double.NaN;
                double interval = 1.0 / Math.Clamp(cfg.Profile.FrameRate, 10, 120);

                SourceSpec spec = cfg.Calibrating ? SourceSpec.ScreenSource : cfg.Source;
                string key = spec.Kind == SourceKind.Screen ? $"screen:{cfg.MonitorDeviceName}" : spec.Key;
                if (source is null || sourceKey != key)
                {
                    source?.Dispose();
                    source = CreateSource(spec, cfg.MonitorDeviceName);
                    sourceKey = key;
                    haveFrame = false;
                    bars.Reset();
                }

                if (!ReferenceEquals(geometryFor, cfg))
                {
                    geometryFor = cfg;
                    placements = LayoutGeometry.Placements(cfg.Layout);
                    closedLoop = cfg.Layout.IsClosedLoop;
                    int physical = Math.Clamp(cfg.StripLedCount > 0 ? cfg.StripLedCount : placements.Length, 0, LedRange.MaxLed);
                    physicalToLayout = LayoutGeometry.PhysicalToLayout(physical, placements.Length);
                    zoneBounds = LightStrip.ZoneBoundaries(physical);
                    if (samples.Length != placements.Length)
                    {
                        samples = new Vector3[placements.Length];
                        rects = new PixelRect[placements.Length];
                        fadeRects = new PixelRect[placements.Length];
                    }

                    rectsDirty = true;
                }

                bool detectBars = cfg.Profile.DetectBlackBars && source is not BuiltInEffectSource && !cfg.Calibrating; // test patterns have no bars
                bool fading = loopStart - fadeStart < BarFadeSeconds;

                bool idle = haveFrame && pipeline.Converged && !_forceSend && chase is null && !fading;
                CaptureStatus status = source.Next(frame, loopStart, idle ? 40 : 0, spec);
                double now = clock.Elapsed.TotalSeconds;
                long workStart = Stopwatch.GetTimestamp();

                bool newPicture = false;
                if (status == CaptureStatus.NewFrame)
                {
                    haveFrame = true;
                    captured++;
                    newPicture = true;
                    PixelRect area = bars.Update(frame, now, detectBars);
                    if (area != content)
                    {
                        // Bars appeared or went: fade from the old picture area instead of jumping.
                        if (content.Area > 0 && detectBars)
                        {
                            fadeFrom = content;
                            fadeStart = now;
                        }

                        content = area;
                        rectsDirty = true;
                    }
                }
                else if (status == CaptureStatus.Unavailable)
                {
                    _stats = _stats with { Error = source.LastError };
                    sleeper.Sleep(haveFrame ? 100 : 250);
                    if (!haveFrame)
                    {
                        continue;
                    }
                }

                if (!haveFrame)
                {
                    sleeper.Sleep(interval * 1000);
                    continue;
                }

                // The preset's filters work on a copy, so changing them re-filters the last picture.
                if (newPicture || !ReferenceEquals(filteredFor, cfg))
                {
                    filteredFor = cfg;
                    if (cfg.Profile.HasFilters)
                    {
                        filters.Apply(frame, filtered, cfg.Profile);
                        filtered.Sequence = --filteredSequence; // never equal to a source frame's number
                        picture = filtered;
                    }
                    else
                    {
                        picture = frame;
                    }

                    analyzer.Load(picture);
                    newPicture = true;
                }

                fading = now - fadeStart < BarFadeSeconds;
                if (newPicture || rectsDirty || fading)
                {
                    if (rectsDirty)
                    {
                        rectsDirty = false;
                        ZoneArea[] areas = LayoutGeometry.Areas(cfg.Layout, cfg.Profile, Aspect(content));
                        ZoneArea[] fadeAreas = LayoutGeometry.Areas(cfg.Layout, cfg.Profile, Aspect(fadeFrom));
                        for (int i = 0; i < placements.Length; i++)
                        {
                            int zone = placements[i].Segment;
                            rects[i] = Sample(placements[i], areas[zone], content, cfg.Calibrating);
                            fadeRects[i] = Sample(placements[i], fadeAreas[zone], fadeFrom, cfg.Calibrating);
                        }
                    }

                    float k = fading ? (float)SmoothStep((now - fadeStart) / BarFadeSeconds) : 1f;
                    for (int i = 0; i < placements.Length; i++)
                    {
                        if (!placements[i].IsMapped)
                        {
                            samples[i] = Vector3.Zero;
                            continue;
                        }

                        Vector3 sample = analyzer.Average(rects[i], cfg.Profile.ColorFocus);
                        samples[i] = k < 1f ? Vector3.Lerp(analyzer.Average(fadeRects[i], cfg.Profile.ColorFocus), sample, k) : sample;
                    }
                }

                double dt = lastProcess > 0 ? Math.Min(now - lastProcess, 0.25) : interval;
                lastProcess = now;
                pipeline.Process(samples, placements, physicalToLayout, closedLoop, cfg.Profile, cfg.Calibration, dt);
                ReadOnlySpan<byte> output = pipeline.Output;
                if (chase is not null)
                {
                    // A zone is being shown: the strip and the live view show it instead of the picture.
                    if (chaseOutput.Length != output.Length)
                    {
                        chaseOutput = new byte[output.Length];
                    }

                    chase.Render(chaseOutput, chase.Elapsed);
                    output = chaseOutput;
                }

                processed++;
                processTime += Stopwatch.GetElapsedTime(workStart).TotalMilliseconds;

                if (sending)
                {
                    bool changed = _forceSend || !output.SequenceEqual(lastSent);
                    if (changed || now - lastSend >= KeepAliveSeconds)
                    {
                        int count = chase is not null
                            ? BuildExactRanges(output, cfg.Calibration, zoneBounds, ref ranges)
                            : BuildRanges(output, cfg.Calibration, zoneBounds, ref ranges);
                        lock (_sendGate)
                        {
                            // Re-check under the gate: sending may have been switched off meanwhile.
                            if (_sendEnabled && !_paused && strip!.SendSyncFrame(ranges.AsSpan(0, count)))
                            {
                                sent++;
                                lastSend = now;
                                _forceSend = false;
                                if (lastSent.Length != output.Length)
                                {
                                    lastSent = new byte[output.Length];
                                }

                                output.CopyTo(lastSent);
                            }
                        }
                    }
                }

                if (_previewEnabled && now - lastPreview >= PreviewInterval)
                {
                    lastPreview = now;
                    if (lut is null || !ReferenceEquals(lutFor, cfg))
                    {
                        lut = PreviewLut.Build(cfg.Profile, cfg.Calibration);
                        lutFor = cfg;
                        lutVersion++;
                    }

                    Publish(picture, lut, lutVersion, content, rects, placements, pipeline, chase is not null ? chaseOutput : pipeline.PreviewOutput, sending);
                }

                if (now - statsStart >= 1.0)
                {
                    double span = now - statsStart;
                    _stats = new SyncStats(
                        captured / span,
                        sent / span,
                        processed > 0 ? processTime / processed : 0,
                        sending,
                        source.Label,
                        status == CaptureStatus.Unavailable ? source.LastError : null,
                        source.RefreshRate,
                        pipeline.PowerScale);
                    statsStart = now;
                    captured = sent = processed = 0;
                    processTime = 0;

                    if (now - lastStatsLog >= 60)
                    {
                        lastStatsLog = now;
                        SyncStats s = _stats;
                        Log.Info($"Sync ({s.SourceLabel}): {(s.RefreshRate > 0 ? $"screen {s.RefreshRate:0} Hz, " : "")}picture {s.CaptureFps:0.0}/s, strip {(s.Sending ? $"{s.SendFps:0.0}/s" : "idle")}, processing {s.ProcessMs:0.00} ms/frame");
                    }
                }

                if (!idle || status == CaptureStatus.NewFrame)
                {
                    // Pace against fixed deadlines so timer overshoot does not accumulate; after a
                    // stall, restart from now rather than bursting frames to catch up.
                    double after = clock.Elapsed.TotalSeconds;
                    nextDeadline = Math.Max(nextDeadline + interval, after);
                    double remaining = nextDeadline - after;
                    if (remaining > 0.0005)
                    {
                        sleeper.Sleep(remaining * 1000);
                    }
                }
                else
                {
                    nextDeadline = clock.Elapsed.TotalSeconds;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("The sync engine stopped unexpectedly", ex);
            _stats = SyncStats.Idle with { Error = ex.Message };
        }
        finally
        {
            source?.Dispose();
        }
    }

    private static IFrameSource CreateSource(SourceSpec spec, string? monitorDeviceName)
    {
        if (spec.Kind == SourceKind.Screen)
        {
            return new ScreenSource(monitorDeviceName);
        }

        if (spec.VideoPath is not null)
        {
            return new VideoSource(spec.VideoPath, Path.GetFileNameWithoutExtension(spec.VideoPath));
        }

        return new BuiltInEffectSource(BuiltInEffects.Find(spec.EffectId) ?? BuiltInEffects.All[0]);
    }

    /// <summary>
    /// Converts per-LED bytes into one sync frame: LEDs are averaged into a fixed set of zones
    /// (at most 50, the most one packet can carry) and channels are reordered. Every frame has the
    /// same zones and the same size, exactly like DX Light's frames: frames whose size changed from
    /// one to the next (from merging equal neighbors) made the start of the strip flicker.
    /// </summary>
    internal static int BuildRanges(ReadOnlySpan<byte> output, CalibrationSettings calibration, int[] zoneBounds, ref LedRange[] ranges)
    {
        int leds = output.Length / 3;
        int zones = zoneBounds.Length - 1;
        if (ranges.Length < zones)
        {
            ranges = new LedRange[zones];
        }

        int[] order = ChannelOrders[(int)calibration.ColorOrder];
        int count = 0;
        Span<int> c = stackalloc int[3];
        for (int z = 0; z < zones; z++)
        {
            int start = zoneBounds[z];
            int end = Math.Min(leds, zoneBounds[z + 1]);
            if (end <= start)
            {
                continue;
            }

            c.Clear();
            for (int p = start; p < end; p++)
            {
                c[0] += output[p * 3];
                c[1] += output[p * 3 + 1];
                c[2] += output[p * 3 + 2];
            }

            int size = end - start;
            ranges[count++] = new LedRange(start + 1, end, (byte)(c[order[0]] / size), (byte)(c[order[1]] / size), (byte)(c[order[2]] / size));
        }

        return count;
    }

    /// <summary>
    /// Like <see cref="BuildRanges"/>, but keeps every LED's own color, for frames with few distinct
    /// colors (a zone being shown): runs of equal LEDs are split until the frame has exactly as many
    /// ranges as every other frame. Falls back to <see cref="BuildRanges"/> when there are more runs.
    /// </summary>
    internal static int BuildExactRanges(ReadOnlySpan<byte> output, CalibrationSettings calibration, int[] zoneBounds, ref LedRange[] ranges)
    {
        int leds = output.Length / 3;
        int target = zoneBounds.Length - 1;
        if (leds == 0 || target < 1)
        {
            return 0;
        }

        // Start LED of each run of equal colors.
        Span<int> starts = stackalloc int[target + 1];
        int runs = 1;
        for (int p = 1; p < leds; p++)
        {
            if (!output.Slice(p * 3, 3).SequenceEqual(output.Slice((p - 1) * 3, 3)))
            {
                if (runs == target)
                {
                    return BuildRanges(output, calibration, zoneBounds, ref ranges);
                }

                starts[runs++] = p;
            }
        }

        // Halve the longest run until the count matches.
        while (runs < target)
        {
            int longest = -1, longestLength = 1;
            for (int r = 0; r < runs; r++)
            {
                int length = (r + 1 < runs ? starts[r + 1] : leds) - starts[r];
                if (length > longestLength)
                {
                    longest = r;
                    longestLength = length;
                }
            }

            if (longest < 0)
            {
                break;
            }

            for (int r = runs; r > longest + 1; r--)
            {
                starts[r] = starts[r - 1];
            }

            starts[longest + 1] = starts[longest] + longestLength / 2;
            runs++;
        }

        if (ranges.Length < runs)
        {
            ranges = new LedRange[runs];
        }

        int[] order = ChannelOrders[(int)calibration.ColorOrder];
        for (int r = 0; r < runs; r++)
        {
            int first = starts[r];
            int end = r + 1 < runs ? starts[r + 1] : leds;
            ReadOnlySpan<byte> c = output.Slice(first * 3, 3);
            ranges[r] = new LedRange(first + 1, end, c[order[0]], c[order[1]], c[order[2]]);
        }

        return runs;
    }

    private static double Aspect(PixelRect content) =>
        content.Width > 0 && content.Height > 0 ? content.Width / (double)content.Height : 16.0 / 9.0;

    /// <summary>One LED's share of its zone's area; near the edge only while the test pattern shows.</summary>
    private static PixelRect Sample(LedPlacement placement, ZoneArea area, PixelRect content, bool calibrating)
    {
        PixelRect rect = LayoutGeometry.SampleRect(placement, area, content);
        return calibrating && rect.Area > 0 ? LayoutGeometry.KeepNearEdge(rect, placement.Edge, content, CalibrationMaxDepth) : rect;
    }

    private static double SmoothStep(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private void Publish(CapturedFrame frame, PreviewLut lut, int lutVersion, PixelRect content, PixelRect[] rects, LedPlacement[] placements, ColorPipeline pipeline, ReadOnlySpan<byte> colors, bool sending)
    {
        if (PreviewReady is null || !_freeSnapshots.TryDequeue(out PreviewSnapshot? snapshot))
        {
            return; // the UI is still busy with earlier snapshots
        }

        if (snapshot.SourceSequence != frame.Sequence || snapshot.LutVersion != lutVersion)
        {
            lut.Apply(frame, snapshot.Frame);
            snapshot.SourceSequence = frame.Sequence;
            snapshot.LutVersion = lutVersion;
        }

        snapshot.Content = content;
        if (snapshot.SampleRects.Length != rects.Length)
        {
            snapshot.SampleRects = new PixelRect[rects.Length];
        }

        rects.CopyTo(snapshot.SampleRects, 0);
        snapshot.Placements = placements;
        if (snapshot.LedColors.Length != colors.Length)
        {
            snapshot.LedColors = new byte[colors.Length];
        }

        colors.CopyTo(snapshot.LedColors);
        snapshot.Sending = sending;
        snapshot.PowerScale = pipeline.PowerScale;
        snapshot.Stats = _stats;
        try
        {
            PreviewReady?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("Preview handler failed", ex);
            _freeSnapshots.Enqueue(snapshot);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        _wake.Dispose();
    }
}
