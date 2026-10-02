using DxDark.Protocol;

namespace DxDark.Tests;

public class ProtocolTests
{
    [Fact]
    public void ReadInfoPacket_MatchesWhatWasSentToTheRealStrip()
    {
        byte[] packet = Packet.Command(0x5A, StripCommand.ReadDeviceInfo);
        Assert.Equal(new byte[] { 0x52, 0x42, 0x06, 0x5A, 0x82, 0x76 }, packet);
    }

    [Fact]
    public void ParsesTheRealStripsDeviceInfoReply()
    {
        // Reply captured from the user's strip (report ID removed).
        byte[] reply = Convert.FromHexString("5242195A820006092001006ECDAB38E851BD5651E501080200");
        StripInfo? info = StripInfo.TryParse(reply);

        Assert.NotNull(info);
        Assert.Equal("000609", info.ModelId);
        Assert.Equal(new Version(1, 8, 2), info.Firmware);
        Assert.Equal(110, info.LedCount);
        Assert.Equal(32, info.DisplaySizeInches);
        Assert.Equal("cdab38e851bd5651", info.Uuid);
        Assert.True(info.SupportsTurnOffCommand);
    }

    [Fact]
    public void StaticColorRangesMatchDxLight()
    {
        // DX Light paints a static color as [1,r,g,b,N] + [N+1,0,0,0,254].
        byte[] packet = Packet.LedRanges(7, [new LedRange(1, 110, 255, 0, 0), new LedRange(111, 254, 0, 0, 0)]);
        Assert.Equal(16, packet[2]);
        Assert.Equal((byte)StripCommand.SetLedRanges, packet[4]);
        Assert.Equal(new byte[] { 1, 255, 0, 0, 110, 111, 0, 0, 0, 254 }, packet[5..15]);
        Assert.Equal(Packet.Checksum(packet.AsSpan(0, 15)), packet[15]);
    }

    [Fact]
    public void SyncFrameHasTwoByteLengthAndChecksum()
    {
        byte[] packet = Packet.SyncFrame(3, [new LedRange(1, 2, 10, 20, 30), new LedRange(3, 3, 40, 50, 60)]);
        Assert.Equal(17, packet.Length);
        Assert.Equal(new byte[] { (byte)'S', (byte)'C', 0x00, 17, 3, 0x80 }, packet[..6]);
        Assert.Equal(new byte[] { 1, 10, 20, 30, 2, 3, 40, 50, 60, 3 }, packet[6..16]);
        Assert.Equal(Packet.Checksum(packet.AsSpan(0, 16)), packet[16]);
    }

    [Fact]
    public void PacketsAreSplitInto65ByteReportsWithReportIdZero()
    {
        byte[] packet = Packet.SyncFrame(1, Enumerable.Range(1, 50).Select(i => LedRange.Single(i, 1, 2, 3)).ToArray());
        Assert.Equal(257, packet.Length);

        List<byte[]> reports = Packet.ToReports(packet);
        Assert.Equal(5, reports.Count);
        Assert.All(reports, r => Assert.Equal(65, r.Length));
        Assert.All(reports, r => Assert.Equal(0, r[0]));
        Assert.Equal(packet[64..128], reports[1][1..65]);
        Assert.Equal(packet[256], reports[4][1]);
        Assert.All(reports[4][2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void LongCommandsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Packet.LedRanges(1, new LedRange[Packet.MaxRangesPerCommand + 1]));
    }

    [Theory]
    [InlineData('S', 'C')]
    [InlineData('R', 'B')]
    public void NoReportStartsWithBytesThatLookLikeAPacketHeader(char first, char second)
    {
        // In a 50-zone frame, byte 128 (the start of the third report) is zone 25's green value
        // and byte 129 its blue value. Make them spell a packet header.
        LedRange[] ranges = Enumerable.Range(0, 50).Select(i => LedRange.Single(i + 1, 10, 20, 30)).ToArray();
        ranges[24] = ranges[24] with { G = (byte)first, B = (byte)second };

        byte[] packet = Packet.SyncFrame(1, ranges);

        Assert.Equal(-1, Packet.FindHeaderLookalike(packet));
        Assert.Equal((byte)first, packet[128]);
        Assert.InRange(packet[129], (byte)(second - 1), (byte)(second + 1)); // nudged by one step at most
        Assert.Equal(Packet.Checksum(packet.AsSpan(0, packet.Length - 1)), packet[^1]);
    }

    [Fact]
    public void OrdinaryFramesAreLeftUntouched()
    {
        LedRange[] ranges = Enumerable.Range(0, 50).Select(i => LedRange.Single(i + 1, (byte)i, (byte)(i * 2), (byte)(i * 3))).ToArray();
        byte[] packet = Packet.SyncFrame(1, ranges);
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(ranges[i].G, packet[6 + i * 5 + 2]);
        }
    }
}
