namespace DxDark.Protocol;

/// <summary>USB identifiers of the light strip controller.</summary>
public static class StripUsb
{
    /// <summary>QinHeng Electronics (WCH), the maker of the controller's USB chip.</summary>
    public const ushort VendorId = 0x1A86;

    public const ushort ProductId = 0xFE07;

    /// <summary>
    /// The controller exposes a vendor-defined HID collection for commands. It also exposes a
    /// keyboard collection (used by its "open URL" feature), which is never opened.
    /// </summary>
    public const ushort CommandUsagePage = 0xFF00;
}

/// <summary>Command codes understood by the controller (byte 4 of an "RB" packet, byte 5 of an "SC" packet).</summary>
public enum StripCommand : byte
{
    /// <summary>Screen-sync frame: a list of colored LED ranges, sent continuously while syncing.</summary>
    SyncFrame = 0x80,

    /// <summary>Writes the stored setup (model code, display size, LED count). Persistent.</summary>
    WriteDeviceInfo = 0x81,

    ReadDeviceInfo = 0x82,
    ReadDeviceUuid = 0x83,

    /// <summary>Starts a built-in effect: payload [kind, index].</summary>
    SetEffect = 0x85,

    /// <summary>Paints LED ranges with fixed colors (used for static colors).</summary>
    SetLedRanges = 0x86,

    /// <summary>Hardware brightness, 5–255.</summary>
    SetBrightness = 0x87,

    SetAutoOff = 0x89,

    /// <summary>Built-in effect speed as a delay: 0 is fastest, 100 slowest.</summary>
    SetEffectSpeed = 0x8A,

    /// <summary>Microphone sensitivity for rhythm effects, 0–100.</summary>
    SetMicSensitivity = 0x8B,

    /// <summary>Enables/disables the controller typing a web address through its keyboard interface.</summary>
    SetOpenUrl = 0x93,

    SetLedCount = 0x95,

    /// <summary>Turns the LEDs off (firmware 1.8.0 and later).</summary>
    TurnOff = 0x97,

    /// <summary>Sent by the controller when its buttons switch screen sync on (1) or off (0).</summary>
    SyncStatusReport = 0xF1,
}

/// <summary>Built-in effect families stored in the controller's firmware.</summary>
public enum EffectKind : byte
{
    /// <summary>Animated color effects (DX Light calls these "Dynamic").</summary>
    Dynamic = 2,

    /// <summary>Music effects driven by the controller's own microphone (DX Light: "Rhythm").</summary>
    Rhythm = 3,
}
