namespace DxDark.Capture;

public enum DisplayRotation
{
    Identity,
    Rotate90,
    Rotate180,
    Rotate270,
}

/// <summary>A monitor attached to the desktop.</summary>
/// <param name="DeviceName">GDI device name such as <c>\\.\DISPLAY1</c>; stable across restarts.</param>
/// <param name="FriendlyName">Monitor model name when Windows knows it.</param>
public sealed record MonitorInfo(
    string DeviceName,
    string FriendlyName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    DisplayRotation Rotation,
    string AdapterName)
{
    public string Label
    {
        get
        {
            string number = DeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase)
                ? DeviceName[@"\\.\DISPLAY".Length..]
                : DeviceName;
            string name = string.IsNullOrWhiteSpace(FriendlyName) ? $"Display {number}" : $"{FriendlyName} (Display {number})";
            return $"{name} · {Width}×{Height}{(IsPrimary ? " · primary" : "")}";
        }
    }
}
