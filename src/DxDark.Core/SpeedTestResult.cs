namespace DxDark.Core;

/// <param name="FramesPerSecond">Sync frames the strip accepted per second (capped at 120 by the test).</param>
/// <param name="Zones">Color zones per frame.</param>
/// <param name="SlowestFrameMs">Longest time one frame took to send, after the first few.</param>
/// <param name="StillResponding">Whether the strip answered a status request after the test.</param>
public sealed record SpeedTestResult(double FramesPerSecond, int Zones, double SlowestFrameMs, bool StillResponding);
