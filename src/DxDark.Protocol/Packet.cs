using System.Buffers.Binary;

namespace DxDark.Protocol;

/// <summary>
/// Builds controller packets. Two layouts exist:
/// <list type="bullet">
/// <item>"RB" command: 'R' 'B' length(1) sequence command payload… checksum</item>
/// <item>"SC" sync frame: 'S' 'C' length(2, big-endian) sequence 0x80 ranges… checksum</item>
/// </list>
/// The length counts every byte including the checksum, and the checksum is the sum of all
/// preceding bytes modulo 256.
/// </summary>
public static class Packet
{
    /// <summary>Data bytes per HID report; each report is preceded by report ID 0.</summary>
    public const int ReportDataLength = 64;

    /// <summary>Most ranges that fit in one "RB" packet, whose length field is a single byte.</summary>
    public const int MaxRangesPerCommand = (255 - 6) / 5;

    /// <summary>
    /// Most ranges in one "SC" sync frame. Tested on firmware 1.8.2:
    /// <list type="bullet">
    /// <item>50 ranges (257 bytes, as DX Light sends on a 16:9 monitor) work smoothly at 60+ fps;</item>
    /// <item>one 110-range packet (557 bytes) hung the controller until it was power-cycled;</item>
    /// <item>splitting a frame over several packets makes the start of the strip flicker, because
    /// every sync packet is treated as a whole frame.</item>
    /// </list>
    /// So every frame is a single packet of at most 50 ranges; longer strips share color zones.
    /// </summary>
    public const int MaxRangesPerSyncPacket = 50;

    public static byte Checksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (byte b in data)
        {
            sum += b;
        }

        return (byte)sum;
    }

    public static byte[] Command(byte sequence, StripCommand command, ReadOnlySpan<byte> payload = default)
    {
        int length = 6 + payload.Length;
        if (length > byte.MaxValue)
        {
            throw new ArgumentException($"Payload of {payload.Length} bytes is too long for a command packet.", nameof(payload));
        }

        var packet = new byte[length];
        packet[0] = (byte)'R';
        packet[1] = (byte)'B';
        packet[2] = (byte)length;
        packet[3] = sequence;
        packet[4] = (byte)command;
        payload.CopyTo(packet.AsSpan(5));
        packet[^1] = Checksum(packet.AsSpan(0, length - 1));
        return packet;
    }

    /// <summary>Static LED colors ("RB" 0x86). At most <see cref="MaxRangesPerCommand"/> ranges.</summary>
    public static byte[] LedRanges(byte sequence, ReadOnlySpan<LedRange> ranges)
    {
        if (ranges.Length > MaxRangesPerCommand)
        {
            throw new ArgumentException($"At most {MaxRangesPerCommand} ranges fit in one packet.", nameof(ranges));
        }

        Span<byte> payload = stackalloc byte[ranges.Length * 5];
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i].WriteTo(payload.Slice(i * 5, 5));
        }

        byte[] packet = Command(sequence, StripCommand.SetLedRanges, payload);
        AvoidHeaderLookalikes(packet, rangesOffset: 5);
        return packet;
    }

    /// <summary>A screen-sync frame ("SC" 0x80).</summary>
    public static byte[] SyncFrame(byte sequence, ReadOnlySpan<LedRange> ranges)
    {
        int length = 7 + ranges.Length * 5;
        if (length > ushort.MaxValue)
        {
            throw new ArgumentException("Too many ranges for one sync frame.", nameof(ranges));
        }

        var packet = new byte[length];
        packet[0] = (byte)'S';
        packet[1] = (byte)'C';
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = sequence;
        packet[5] = (byte)StripCommand.SyncFrame;
        for (int i = 0; i < ranges.Length; i++)
        {
            ranges[i].WriteTo(packet.AsSpan(6 + i * 5, 5));
        }

        AvoidHeaderLookalikes(packet, rangesOffset: 6);
        return packet;
    }

    /// <summary>
    /// Makes sure no follow-on HID report starts with "RB" or "SC". The controller recognises
    /// packets by those two bytes, so a report that happened to begin with them (when a color value
    /// lands there) could be mistaken for a new packet and garble the frame. When that would
    /// happen, one color byte is changed by 1, which is invisible. Also writes the checksum.
    /// </summary>
    internal static void AvoidHeaderLookalikes(byte[] packet, int rangesOffset)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            packet[^1] = Checksum(packet.AsSpan(0, packet.Length - 1));
            int clash = FindHeaderLookalike(packet);
            if (clash < 0)
            {
                return;
            }

            int target = IsColorByte(clash + 1, rangesOffset, packet.Length) ? clash + 1
                : IsColorByte(clash, rangesOffset, packet.Length) ? clash
                : rangesOffset + 1; // the clash involves the checksum: change any color to change it
            if (!IsColorByte(target, rangesOffset, packet.Length))
            {
                return;
            }

            packet[target] ^= 1;
        }
    }

    /// <summary>Offset of the first report boundary whose two bytes read "RB" or "SC", or -1.</summary>
    internal static int FindHeaderLookalike(ReadOnlySpan<byte> packet)
    {
        for (int offset = ReportDataLength; offset + 1 < packet.Length; offset += ReportDataLength)
        {
            byte a = packet[offset], b = packet[offset + 1];
            if ((a == (byte)'R' && b == (byte)'B') || (a == (byte)'S' && b == (byte)'C'))
            {
                return offset;
            }
        }

        return -1;
    }

    /// <summary>True for the R, G or B byte of a range (ranges are [first, R, G, B, last]).</summary>
    private static bool IsColorByte(int index, int rangesOffset, int length) =>
        index >= rangesOffset && index < length - 1 && (index - rangesOffset) % 5 is 1 or 2 or 3;

    /// <summary>
    /// Splits a packet into HID output reports: report ID 0 followed by up to 64 packet bytes,
    /// zero-padded to the device's output report length (65 bytes for this controller).
    /// </summary>
    public static List<byte[]> ToReports(ReadOnlySpan<byte> packet, int outputReportLength = ReportDataLength + 1)
    {
        int dataPerReport = Math.Min(ReportDataLength, outputReportLength - 1);
        var reports = new List<byte[]>((packet.Length + dataPerReport - 1) / dataPerReport);
        for (int offset = 0; offset < packet.Length; offset += dataPerReport)
        {
            var report = new byte[outputReportLength];
            int count = Math.Min(dataPerReport, packet.Length - offset);
            packet.Slice(offset, count).CopyTo(report.AsSpan(1));
            reports.Add(report);
        }

        return reports;
    }
}
