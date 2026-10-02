namespace DxDark.Core.Layout;

/// <summary>
/// Factory LED counts per side for the DX Light backlight family, taken from DX Light's own
/// device catalogue. The controller reports its model ID, display size and LED count, so the
/// right default layout can be picked automatically.
/// </summary>
public static class StripCatalog
{
    private static readonly Dictionary<string, Dictionary<int, (int Horizontal, int Vertical)>> SidesByModel = new()
    {
        ["000603"] = new() { [24] = (26, 14), [27] = (29, 17), [32] = (35, 20), [34] = (41, 17) },
        ["000609"] = new() { [24] = (29, 17), [27] = (29, 17), [28] = (36, 18), [32] = (35, 20), [34] = (41, 17) },
        ["00060c"] = new() { [24] = (27, 15), [27] = (30, 18), [32] = (36, 21), [34] = (42, 18) },
        ["00060d"] = new() { [24] = (27, 15), [27] = (30, 18), [28] = (37, 19), [32] = (36, 21), [34] = (42, 18) },
    };

    public static bool IsKnownModel(string modelId) => SidesByModel.ContainsKey(modelId);

    /// <summary>
    /// LEDs per horizontal and vertical side for a known kit, or null. A size entry is only used when
    /// its total matches the LED count the strip reports.
    /// </summary>
    public static (int Horizontal, int Vertical)? Lookup(string modelId, int displaySizeInches, int ledCount)
    {
        if (!SidesByModel.TryGetValue(modelId, out var sizes))
        {
            return null;
        }

        if (sizes.TryGetValue(displaySizeInches, out var exact) && 2 * (exact.Horizontal + exact.Vertical) == ledCount)
        {
            return exact;
        }

        foreach (var entry in sizes.Values)
        {
            if (2 * (entry.Horizontal + entry.Vertical) == ledCount)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Splits LEDs over four sides in proportion to the screen's aspect ratio.</summary>
    public static (int Horizontal, int Vertical) Estimate(int ledCount, double aspectRatio)
    {
        aspectRatio = aspectRatio > 0.2 ? aspectRatio : 16.0 / 9.0;
        int horizontal = (int)Math.Round(ledCount * aspectRatio / (2 * (1 + aspectRatio)));
        int vertical = Math.Max(0, (ledCount - 2 * horizontal) / 2);
        return (horizontal, vertical);
    }

    /// <summary>LEDs per horizontal and vertical side: the kit's factory counts if known, else an estimate.</summary>
    public static (int Horizontal, int Vertical) Sides(string? modelId, int displaySizeInches, int ledCount, double aspectRatio) =>
        (modelId is null ? null : Lookup(modelId, displaySizeInches, ledCount)) ?? Estimate(ledCount, aspectRatio);

    /// <summary>The default layout for a strip: its factory layout if known, else an estimate.</summary>
    public static LedLayout DefaultLayout(string? modelId, int displaySizeInches, int ledCount, double aspectRatio)
    {
        (int h, int v) = Sides(modelId, displaySizeInches, ledCount, aspectRatio);
        LedLayout layout = QuickLayout.FourSided(h, v);

        // Any remainder from the estimate goes to the last (bottom) side.
        int remainder = ledCount - layout.LedCount;
        if (remainder != 0 && layout.Segments.Count > 0)
        {
            layout.Segments[^1].LedCount = Math.Max(0, layout.Segments[^1].LedCount + remainder);
        }

        return layout;
    }
}
