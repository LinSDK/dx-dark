using System.Buffers.Binary;

namespace DxDark.Protocol;

/// <summary>
/// The setup stored in the controller, as returned by <see cref="StripCommand.ReadDeviceInfo"/>.
/// This survives power cycles; colors and effects chosen from the PC do not.
/// </summary>
/// <param name="FactoryCode">Manufacturer code (0x0006 for the DX Light backlight family).</param>
/// <param name="DeviceCode">Model code within the manufacturer.</param>
/// <param name="DisplaySizeInches">Monitor size the kit was made for (24, 27, 32, …).</param>
/// <param name="Flags">Unknown; always written as 1 by DX Light.</param>
/// <param name="LedCount">Number of LEDs on the strip.</param>
/// <param name="Uuid">Unique device identifier (16 hex digits).</param>
/// <param name="Firmware">Firmware version.</param>
public sealed record StripInfo(
    ushort FactoryCode,
    byte DeviceCode,
    byte DisplaySizeInches,
    byte Flags,
    int LedCount,
    string Uuid,
    Version Firmware)
{
    /// <summary>The 6-digit model ID DX Light uses, e.g. "000609".</summary>
    public string ModelId => $"{FactoryCode:x4}{DeviceCode:x2}";

    /// <summary>Firmware 1.8.0 added a dedicated "lights off" command.</summary>
    public bool SupportsTurnOffCommand => Firmware >= new Version(1, 8, 0);

    /// <summary>
    /// Parses a device-info reply. <paramref name="reply"/> starts at the packet header
    /// (the HID report ID already removed).
    /// </summary>
    public static StripInfo? TryParse(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 24
            || reply[0] != (byte)'R' || reply[1] != (byte)'B'
            || reply[4] != (byte)StripCommand.ReadDeviceInfo)
        {
            return null;
        }

        return new StripInfo(
            FactoryCode: BinaryPrimitives.ReadUInt16BigEndian(reply[5..]),
            DeviceCode: reply[7],
            DisplaySizeInches: reply[8],
            Flags: reply[9],
            LedCount: BinaryPrimitives.ReadUInt16BigEndian(reply[10..]),
            Uuid: Convert.ToHexStringLower(reply[12..20]),
            Firmware: new Version(reply[21], reply[22], reply[23]));
    }
}
