using System.Diagnostics;
using DxDark.Capture;
using DxDark.Core.Color;
using DxDark.Core.Effects;
using DxDark.Core.Layout;
using DxDark.Core.Settings;
using DxDark.Core.Sync;
using DxDark.Protocol;

namespace DxDark.Core;

/// <summary>
/// The app's brain: keeps the strip connected (reconnecting after unplug/replug), applies the
/// selected mode, feeds settings to the sync engine, and handles zones, calibration, lock/sleep
/// and the strip's own buttons. Public methods are called from the UI thread; all USB traffic
/// happens on the command thread or the sync engine thread.
/// </summary>
public sealed class LightController : IDisposable
{
    private const int ReconnectIntervalMs = 2000;

    private readonly SettingsStore _store;
    private readonly CommandQueue _commands = new();
    private readonly SyncEngine _engine;
    private readonly Timer _watchTimer;
    private readonly HashSet<string> _suspendReasons = [];
    private readonly object _suspendLock = new();
    private volatile LightStrip? _strip;
    private volatile bool _calibrating;
    private volatile bool _speedTestRunning;
    private volatile bool _shuttingDown;
    private bool _warnedAboutDxLight;
    private double _aspectRatio;
    private long _aspectReadAt;

    public LightController(SettingsStore store)
    {
        _store = store;
        _engine = new SyncEngine(() => _strip, BuildSyncConfig());
        _watchTimer = new Timer(_ => _commands.Post(TryConnect, "connect"), null, Timeout.Infinite, Timeout.Infinite);
    }

    public AppSettings Settings => _store.Current;

    public SettingsStore Store => _store;

    public SyncEngine Engine => _engine;

    /// <summary>Folder holding the videos added as effects.</summary>
    public string VideosDirectory => Path.Combine(_store.Directory, "Videos");

    /// <summary>Info of the connected strip, or null while disconnected.</summary>
    public StripInfo? StripInfo { get; private set; }

    public bool IsConnected => _strip is { IsConnected: true };

    /// <summary>Why the strip is not usable, when it is plugged in but misbehaving.</summary>
    public string? ConnectionProblem { get; private set; }

    public bool IsCalibrating => _calibrating;

    public bool IsSuspended
    {
        get
        {
            lock (_suspendLock)
            {
                return _suspendReasons.Count > 0;
            }
        }
    }

    /// <summary>LED count of the connected strip, else of the strip last connected, else of the common 32" kit.</summary>
    public int StripLedCount => StripInfo?.LedCount ?? (Settings.Strip.LedCount > 0 ? Settings.Strip.LedCount : Math.Max(110, Settings.Layout.LedCount));

    /// <summary>True when the strip's LED count is known (connected now or before).</summary>
    public bool IsStripKnown => StripInfo is not null || Settings.Strip.LedCount > 0;

    /// <summary>Connection, mode or strip info changed. Raised on a background thread.</summary>
    public event Action? StateChanged;

    /// <summary>All settings were replaced (the settings file was edited). Raised on the calling thread.</summary>
    public event Action? LayoutReplaced;

    /// <summary>A message worth showing the user (e.g. as a tray notification).</summary>
    public event Action<string>? Notice;

    public void Start()
    {
        Log.Info("DX Dark started");
        _watchTimer.Change(0, ReconnectIntervalMs);
    }

    // ── Mode and effects ───────────────────────────────────────────────────────

    public void SetMode(LightMode mode)
    {
        Settings.Lighting.Mode = mode;
        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
        ApplyModeSoon();
        RaiseStateChanged();
    }

    /// <summary>Selects an effect (built-in or video) and switches to effect mode.</summary>
    public void SetEffect(string effectId)
    {
        Settings.Lighting.EffectId = effectId;
        SetMode(LightMode.Effect);
    }

    public void SetEffectSpeed(double speed)
    {
        Settings.Lighting.EffectSpeed = Math.Clamp(speed, 0.25, 3);
        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
    }

    public void SetSolidColor(string hex)
    {
        if (ColorMath.TryParseHex(hex, out byte r, out byte g, out byte b))
        {
            Settings.Lighting.SolidColor = ColorMath.ToHex(r, g, b);
            _store.SaveSoon();
            _engine.Configure(BuildSyncConfig());
        }
    }

    /// <summary>
    /// Copies a video into the effects library after checking that Windows can play it. Slow for
    /// large files: call off the UI thread.
    /// </summary>
    public VideoEffect AddVideo(string path)
    {
        using (var probe = new VideoReader(path))
        {
            var test = new CapturedFrame();
            if (!probe.ReadFrame(test, out _))
            {
                throw new InvalidOperationException("The video has no frames Windows can decode.");
            }
        }

        Directory.CreateDirectory(VideosDirectory);
        var video = new VideoEffect { Name = Path.GetFileNameWithoutExtension(path) };
        video.FileName = video.Id + Path.GetExtension(path).ToLowerInvariant();
        File.Copy(path, Path.Combine(VideosDirectory, video.FileName));
        lock (Settings.Lighting.Videos)
        {
            Settings.Lighting.Videos.Add(video);
        }

        _store.SaveSoon();
        Log.Info($"Video effect added: {video.Name}");
        return video;
    }

    public void RemoveVideo(string videoId)
    {
        VideoEffect? video = Settings.Lighting.Videos.FirstOrDefault(v => v.Id == videoId);
        if (video is null)
        {
            return;
        }

        lock (Settings.Lighting.Videos)
        {
            Settings.Lighting.Videos.Remove(video);
        }

        if (Settings.Lighting.EffectId == videoId)
        {
            Settings.Lighting.EffectId = BuiltInEffects.Rainbow;
        }

        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
        try
        {
            File.Delete(VideoPath(video));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {video.FileName}: {ex.Message}"); // still in use; harmless
        }

        RaiseStateChanged();
    }

    public string VideoPath(VideoEffect video) => Path.Combine(VideosDirectory, video.FileName);

    // ── Presets, zones, calibration ────────────────────────────────────────────

    public void ActivateProfile(string name)
    {
        if (Settings.Profiles.Any(p => p.Name == name))
        {
            Settings.ActiveProfile = name;
            ProfileChanged();
            RaiseStateChanged();
        }
    }

    /// <summary>Call after editing preset values or the preset list.</summary>
    public void ProfileChanged()
    {
        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
    }

    /// <summary>Call after adding, removing or changing zones.</summary>
    public void ZonesChanged()
    {
        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
    }

    /// <summary>Call after editing calibration (gains, color order, power limit).</summary>
    public void CalibrationChanged()
    {
        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
    }

    /// <summary>The strip's own brightness (5–255), applied by the controller.</summary>
    public void SetStripBrightness(int value)
    {
        Settings.Calibration.HardwareBrightness = Math.Clamp(value, 5, 255);
        _store.SaveSoon();
        _commands.Post(() => _strip?.SetBrightness(Settings.Calibration.HardwareBrightness), "brightness");
    }

    public void SetMonitor(string? deviceName)
    {
        Settings.MonitorDeviceName = deviceName;
        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
    }

    /// <summary>
    /// LEDs per horizontal and vertical edge for the strip: its kit's factory counts when known,
    /// else an estimate from the monitor's shape. A guide for setting up zones.
    /// </summary>
    public (int Horizontal, int Vertical) EdgeLedCounts()
    {
        StripInfo? info = StripInfo;
        string? model = info?.ModelId ?? Settings.Strip.ModelId;
        int size = info?.DisplaySizeInches ?? Settings.Strip.DisplaySizeInches;
        return StripCatalog.Sides(model, size, StripLedCount, MonitorAspectRatio());
    }

    /// <summary>Width / height of the synced monitor.</summary>
    public double ScreenAspect => MonitorAspectRatio();

    /// <summary>True when <see cref="EdgeLedCounts"/> comes from the kit's factory counts rather than an estimate.</summary>
    public bool EdgeLedCountsAreExact()
    {
        string? model = StripInfo?.ModelId ?? Settings.Strip.ModelId;
        int size = StripInfo?.DisplaySizeInches ?? Settings.Strip.DisplaySizeInches;
        return model is not null && StripCatalog.Lookup(model, size, StripLedCount) is not null;
    }

    /// <summary>
    /// Shows zone <paramref name="zoneIndex"/> on the strip until <see cref="StopShowingZone"/>: its
    /// LEDs glow while a red dot keeps running from its first LED to its last. With
    /// <paramref name="dotDelaySeconds"/>, only the zone's extent is shown at first (while its LED
    /// count is being dragged).
    /// </summary>
    public void ShowZone(int zoneIndex, double dotDelaySeconds = 0)
    {
        LedLayout layout = Settings.Layout;
        if (zoneIndex < 0 || zoneIndex >= layout.Segments.Count)
        {
            return;
        }

        if (Settings.Lighting.Mode == LightMode.Controller)
        {
            // The strip's own buttons had taken over; showing a zone hands control back to DX Dark.
            SetMode(LightMode.ScreenSync);
        }

        _engine.ShowZone(layout.SegmentStart(zoneIndex), layout.Segments[zoneIndex].LedCount, dotDelaySeconds);
        ApplyModeSoon(); // streams while the zone shows, even if the lights are off
    }

    public void StopShowingZone()
    {
        if (_engine.IsShowingZone)
        {
            _engine.StopShowingZone();
            ApplyModeSoon(); // stop streaming again if the lights were off
        }
    }

    /// <summary>Switches to settings read back from the settings file after it was edited by hand.</summary>
    public void ReplaceSettings(AppSettings settings)
    {
        _engine.StopShowingZone();
        _store.Replace(settings);
        _engine.Configure(BuildSyncConfig());
        _commands.Post(() => _strip?.SetBrightness(Settings.Calibration.HardwareBrightness), "brightness");
        ApplyModeSoon();
        LayoutReplaced?.Invoke();
        RaiseStateChanged();
    }

    public void SetPreviewEnabled(bool enabled) => _engine.PreviewEnabled = enabled;

    /// <summary>While the calibration screen is open the strip follows the screen, whatever the mode.</summary>
    public void BeginCalibration()
    {
        _calibrating = true;
        _engine.Configure(BuildSyncConfig());
        ApplyModeSoon();
        RaiseStateChanged();
    }

    public void EndCalibration()
    {
        _calibrating = false;
        _engine.Configure(BuildSyncConfig());
        ApplyModeSoon();
        RaiseStateChanged();
    }

    /// <summary>
    /// Streams a moving rainbow as fast as the strip accepts it (capped at 120 fps) and reports the
    /// rate. Uses the same single-packet, 50-zone frames as screen sync.
    /// </summary>
    public Task<SpeedTestResult?> RunSpeedTestAsync(double seconds = 3)
    {
        var result = new TaskCompletionSource<SpeedTestResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _speedTestRunning = true;
        _engine.Paused = true;
        _commands.Post(() =>
        {
            try
            {
                _engine.WaitForPendingSend();
                LightStrip? strip = _strip;
                if (strip is null)
                {
                    result.TrySetResult(null);
                    return;
                }

                int[] zones = LightStrip.ZoneBoundaries(StripLedCount);
                var ranges = new LedRange[zones.Length - 1];
                using var sleeper = new Platform.PrecisionSleeper();
                var clock = Stopwatch.StartNew();
                int frames = 0;
                double slowest = 0, deadline = 0;
                while (clock.Elapsed.TotalSeconds < seconds && strip.IsConnected)
                {
                    double t = clock.Elapsed.TotalSeconds;
                    for (int z = 0; z < ranges.Length; z++)
                    {
                        double hue = ((zones[z] + zones[z + 1]) / 2.0 / StripLedCount + t * 0.3) % 1.0;
                        (byte r, byte g, byte b) = SafeHue(hue);
                        ranges[z] = new LedRange(zones[z] + 1, zones[z + 1], r, g, b);
                    }

                    long start = Stopwatch.GetTimestamp();
                    if (!strip.SendSyncFrame(ranges))
                    {
                        break;
                    }

                    if (frames > 2)
                    {
                        slowest = Math.Max(slowest, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }

                    frames++;
                    deadline = Math.Max(deadline + 1000.0 / 120, clock.Elapsed.TotalMilliseconds);
                    sleeper.Sleep(deadline - clock.Elapsed.TotalMilliseconds);
                }

                bool responding = strip.ReadInfo(attempts: 2) is not null;
                result.TrySetResult(new SpeedTestResult(frames / clock.Elapsed.TotalSeconds, ranges.Length, slowest, responding));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
            finally
            {
                _speedTestRunning = false;
                _engine.Paused = false;
                ApplyMode();
            }
        });
        return result.Task;
    }

    /// <summary>A fully saturated hue with R+G+B ≤ 255 (within DX Light's per-LED power cap).</summary>
    private static (byte, byte, byte) SafeHue(double hue)
    {
        double r = Math.Clamp(Math.Abs(hue * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(hue * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(hue * 6 - 4), 0, 1);
        double scale = 255 / Math.Max(1, r + g + b);
        return ((byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
    }

    // ── Lock screen, display off, sleep ──────────────────────────────────────

    public void Suspend(string reason)
    {
        bool changed;
        lock (_suspendLock)
        {
            changed = _suspendReasons.Add(reason);
        }

        if (changed)
        {
            Log.Info($"Lights paused ({reason})");
            ApplyModeSoon();
            RaiseStateChanged();
        }
    }

    public void Resume(string reason)
    {
        bool changed;
        lock (_suspendLock)
        {
            changed = _suspendReasons.Remove(reason);
        }

        if (changed)
        {
            Log.Info($"Lights resumed ({reason})");
            if (reason == "sleep")
            {
                _commands.Post(TryConnect, "connect"); // USB devices often re-enumerate after sleep
            }

            ApplyModeSoon();
            RaiseStateChanged();
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private SyncConfig BuildSyncConfig() => new(
        Settings.GetActiveProfile().Clone(),
        Settings.Layout.Clone(),
        Settings.Calibration.Clone(),
        StripInfo?.LedCount ?? Settings.Layout.LedCount,
        Settings.MonitorDeviceName,
        Settings.Lighting.Mode == LightMode.Effect ? EffectSpec() : SourceSpec.ScreenSource,
        _calibrating);

    private SourceSpec EffectSpec()
    {
        LightingSettings lighting = Settings.Lighting;
        VideoEffect? video;
        lock (lighting.Videos)
        {
            video = lighting.Videos.FirstOrDefault(v => v.Id == lighting.EffectId);
        }

        return new SourceSpec(
            SourceKind.Effect,
            lighting.EffectId,
            video is null ? null : VideoPath(video),
            lighting.EffectSpeed,
            lighting.SolidColor);
    }

    private void ApplyModeSoon() => _commands.Post(ApplyMode, "mode");

    /// <summary>Runs on the command thread.</summary>
    private void ApplyMode()
    {
        if (_shuttingDown)
        {
            return;
        }

        LightMode mode = Settings.Lighting.Mode;
        bool suspended = IsSuspended;
        bool streaming = (mode is LightMode.ScreenSync or LightMode.Effect || _calibrating || _engine.IsShowingZone) && !suspended;

        // Start/stop streaming before talking to the strip, and let any frame in flight finish.
        _engine.SendEnabled = streaming;
        if (!streaming)
        {
            _engine.WaitForPendingSend();
        }

        LightStrip? strip = _strip;
        if (strip is null || _speedTestRunning)
        {
            return;
        }

        if (streaming)
        {
            _engine.Invalidate();
        }
        else if (suspended || mode == LightMode.Off)
        {
            strip.TurnOff();
        }

        // Controller mode: leave the strip to its own buttons.
    }

    /// <summary>Runs on the command thread every couple of seconds while disconnected.</summary>
    private void TryConnect()
    {
        if (_shuttingDown || _strip is { IsConnected: true })
        {
            return;
        }

        DropStrip();
        LightStrip? strip;
        try
        {
            strip = LightStrip.OpenFirst();
        }
        catch (Exception ex)
        {
            Log.Error("Opening the strip failed", ex);
            return;
        }

        if (strip is null)
        {
            SetProblem(null);
            WarnIfDxLightRunning();
            return;
        }

        StripInfo? info = strip.ReadInfo();
        if (info is null)
        {
            strip.Dispose();
            SetProblem("The strip is plugged in but not responding. Unplug it and plug it back in.");
            WarnIfDxLightRunning();
            return;
        }

        strip.Disconnected += () => _commands.Post(HandleDisconnect, "disconnect");
        strip.SyncStatusReported += on => _commands.Post(() => HandleControllerSyncReport(on));
        _strip = strip;
        StripInfo = info;
        SetProblem(null);
        Log.Info($"Strip connected: model {info.ModelId}, firmware {info.Firmware}, {info.LedCount} LEDs, {info.DisplaySizeInches}\" kit");

        strip.SetOpenUrl(false);
        StripMemory memory = Settings.Strip;
        if (memory.LedCount != info.LedCount || memory.ModelId != info.ModelId || memory.DisplaySizeInches != info.DisplaySizeInches)
        {
            memory.LedCount = info.LedCount;
            memory.ModelId = info.ModelId;
            memory.DisplaySizeInches = info.DisplaySizeInches;
            _store.SaveSoon();
        }

        _engine.Configure(BuildSyncConfig());
        strip.SetBrightness(Settings.Calibration.HardwareBrightness);
        ApplyMode();
        RaiseStateChanged();
    }

    private void HandleDisconnect()
    {
        if (_strip is { IsConnected: false })
        {
            Log.Warn("Strip disconnected");
            DropStrip();
            RaiseStateChanged();
        }
    }

    private void HandleControllerSyncReport(bool on)
    {
        // Tested on firmware 1.8.2: every button press (Power, M, music) reports "off", meaning the
        // strip's own controls have taken over, whatever DX Dark was showing.
        Log.Info($"Strip buttons switched sync {(on ? "on" : "off")}");
        LightMode mode = Settings.Lighting.Mode;
        if (on && mode is not (LightMode.ScreenSync or LightMode.Effect))
        {
            Settings.Lighting.Mode = LightMode.ScreenSync;
        }
        else if (!on && mode != LightMode.Controller)
        {
            Settings.Lighting.Mode = LightMode.Controller;
        }
        else
        {
            return;
        }

        _store.SaveSoon();
        _engine.Configure(BuildSyncConfig());
        ApplyMode();
        RaiseStateChanged();
    }

    /// <summary>Shape of the synced monitor; looked up at most every few seconds (the zone guide asks often).</summary>
    private double MonitorAspectRatio()
    {
        if (_aspectRatio > 0 && Stopwatch.GetElapsedTime(_aspectReadAt).TotalSeconds < 10)
        {
            return _aspectRatio;
        }

        double ratio = 16.0 / 9.0;
        try
        {
            List<MonitorInfo> monitors = MonitorEnumerator.List();
            MonitorInfo? monitor = monitors.FirstOrDefault(m => m.DeviceName == Settings.MonitorDeviceName)
                ?? monitors.FirstOrDefault(m => m.IsPrimary)
                ?? monitors.FirstOrDefault();
            if (monitor is { Width: > 0, Height: > 0 })
            {
                ratio = monitor.Width / (double)monitor.Height;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the monitor aspect ratio: {ex.Message}");
        }

        _aspectRatio = ratio;
        _aspectReadAt = Stopwatch.GetTimestamp();
        return ratio;
    }

    private void DropStrip()
    {
        LightStrip? old = _strip;
        _strip = null;
        StripInfo = null;
        old?.Dispose();
    }

    private void SetProblem(string? problem)
    {
        if (ConnectionProblem != problem)
        {
            ConnectionProblem = problem;
            if (problem is not null)
            {
                Log.Warn(problem);
            }

            RaiseStateChanged();
        }
    }

    private void WarnIfDxLightRunning()
    {
        if (_warnedAboutDxLight || !Settings.General.WarnAboutDxLight)
        {
            return;
        }

        if (Process.GetProcessesByName("DX Light").Length > 0)
        {
            _warnedAboutDxLight = true;
            Notice?.Invoke("DX Light is running. Close it (and turn off its start-at-login) so it doesn't fight DX Dark for the strip.");
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("State change handler failed", ex);
        }
    }

    /// <summary>Applies the exit action and releases the strip. Blocks for at most a second.</summary>
    public void Shutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _watchTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _engine.SendEnabled = false;
        _engine.PreviewEnabled = false;
        _commands.Invoke(() =>
        {
            _shuttingDown = true;
            _engine.WaitForPendingSend();
            LightStrip? strip = _strip;
            if (strip is not null && Settings.General.OnExit == ExitAction.TurnOff)
            {
                strip.TurnOff();
                Thread.Sleep(30);
                strip.TurnOff();
            }

            DropStrip();
        }, TimeSpan.FromSeconds(1));
        _store.SaveNow();
        Log.Info("DX Dark stopped");
    }

    public void Dispose()
    {
        Shutdown();
        _watchTimer.Dispose();
        _engine.Dispose();
        _commands.Dispose();
    }
}
