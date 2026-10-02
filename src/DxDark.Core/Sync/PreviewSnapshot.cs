using DxDark.Capture;
using DxDark.Core.Layout;

namespace DxDark.Core.Sync;

/// <summary>Rates and status of the sync engine, refreshed about once a second.</summary>
public sealed record SyncStats(
    double CaptureFps,
    double SendFps,
    double ProcessMs,
    bool Sending,
    string? SourceLabel,
    string? Error,
    double RefreshRate = 0,
    double PowerScale = 1)
{
    public static SyncStats Idle { get; } = new(0, 0, 0, false, null, null);
}

/// <summary>
/// What the strip "sees", for the live view: the sampled picture with the preset's color
/// processing applied, the zone of every LED and the colors intended for each LED. Snapshots
/// from the engine are pooled; return them with <see cref="SyncEngine.Recycle"/>.
/// </summary>
public sealed class PreviewSnapshot
{
    /// <summary>The picture after color processing (what the sliders do).</summary>
    public CapturedFrame Frame { get; } = new();

    /// <summary>Picture area inside the frame (black bars excluded).</summary>
    public PixelRect Content { get; set; }

    /// <summary>Sampling zone of each layout LED, in frame pixels.</summary>
    public PixelRect[] SampleRects { get; set; } = [];

    public LedPlacement[] Placements { get; set; } = [];

    /// <summary>R, G, B per physical LED as intended, before the power limit.</summary>
    public byte[] LedColors { get; set; } = [];

    /// <summary>Whether frames are being sent to the strip right now.</summary>
    public bool Sending { get; set; }

    /// <summary>How much the power limit is dimming the strip (1 = not at all).</summary>
    public double PowerScale { get; set; } = 1;

    public SyncStats Stats { get; set; } = SyncStats.Idle;

    internal long SourceSequence { get; set; } = -1;

    internal int LutVersion { get; set; } = -1;
}
