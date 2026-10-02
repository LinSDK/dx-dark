namespace DxDark.Core.Profiles;

/// <summary>Starting presets. Users can edit them, and "Reset" restores these values.</summary>
public static class BuiltInProfiles
{
    public const string DefaultName = "Balanced";

    public static IReadOnlyList<Profile> All { get; } =
    [
        new Profile
        {
            Name = "Balanced",
        },
        new Profile
        {
            Name = "Cinema",
            SampleDepth = 0.18,
            ZoneOverlap = 1.0,
            ColorFocus = 0.25,
            Smoothing = 320,
            SpatialBlend = 2.0,
            Saturation = 1.05,
            Vibrance = 0.15,
            Gamma = 1.4,
            Temperature = 6000,
            BlackThreshold = 0.06,
        },
        new Profile
        {
            Name = "Gaming",
            SampleDepth = 0.08,
            ZoneOverlap = 0.3,
            ColorFocus = 0.45,
            Smoothing = 45,
            SpatialBlend = 0.6,
            Saturation = 1.2,
            Vibrance = 0.3,
            Gamma = 1.25,
            DetectBlackBars = false,
        },
        new Profile
        {
            Name = "Vivid",
            ColorFocus = 0.8,
            Smoothing = 120,
            SpatialBlend = 1.2,
            Saturation = 1.4,
            Vibrance = 0.45,
            Gamma = 1.5,
        },
        new Profile
        {
            Name = "Soft ambience",
            SampleDepth = 0.28,
            ZoneOverlap = 1.6,
            ColorFocus = 0.3,
            Smoothing = 650,
            SpatialBlend = 3.2,
            Saturation = 1.1,
            Vibrance = 0.2,
            Gamma = 1.35,
            BlackThreshold = 0.04,
        },
        new Profile
        {
            Name = "Faithful",
            ColorFocus = 0,
            Smoothing = 80,
            SpatialBlend = 0.5,
            Saturation = 1.0,
            Vibrance = 0,
            Gamma = 1.0,
            BlackThreshold = 0.02,
        },
    ];

    public static List<Profile> CreateAll() => All.Select(p => p.Clone()).ToList();

    public static Profile? Find(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
