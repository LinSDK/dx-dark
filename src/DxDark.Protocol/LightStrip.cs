using System.Collections.Concurrent;
using DxDark.Protocol.Hid;

namespace DxDark.Protocol;

/// <summary>
/// A connected light strip controller. Thread-safe: packets are written atomically (all reports
/// of one packet back to back), and replies are matched to requests by sequence number.
/// </summary>
public sealed class LightStrip : IDisposable
{
    private readonly HidConnection _hid;
    private readonly object _sendLock = new();
    private readonly object _sequenceLock = new();
    private readonly ConcurrentDictionary<byte, PendingReply> _pending = new();
    private int _sequence = 1;

    private LightStrip(HidConnection hid)
    {
        _hid = hid;
        _hid.ReportReceived += OnReport;
        _hid.Disconnected += () => Disconnected?.Invoke();
    }

    public string DevicePath => _hid.Path;

    public bool IsConnected => _hid.IsOpen;

    /// <summary>Set by <see cref="ReadInfo"/>.</summary>
    public StripInfo? Info { get; private set; }

    /// <summary>The controller's buttons switched screen sync on (true) or off (false).</summary>
    public event Action<bool>? SyncStatusReported;

    /// <summary>Raised once when the strip is unplugged or stops responding to writes.</summary>
    public event Action? Disconnected;

    /// <summary>Every input report that is not a reply to a request (report ID removed).</summary>
    public event Action<byte[]>? UnsolicitedReport;

    /// <summary>Finds the controller's command interface on all connected strips.</summary>
    public static IReadOnlyList<HidDeviceInfo> FindDevices() =>
        HidEnumerator.Enumerate(StripUsb.VendorId, StripUsb.ProductId)
            .Where(d => d.UsagePage == StripUsb.CommandUsagePage)
            .ToList();

    public static LightStrip Open(HidDeviceInfo device) => new(HidConnection.Open(device.Path));

    /// <summary>Opens the first strip found, or returns null if none is connected.</summary>
    public static LightStrip? OpenFirst()
    {
        foreach (HidDeviceInfo device in FindDevices())
        {
            try
            {
                return Open(device);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Try the next one (e.g. a stale interface during re-enumeration).
            }
        }

        return null;
    }

    public byte NextSequence()
    {
        lock (_sequenceLock)
        {
            // Same rotation as DX Light: 1..254.
            _sequence = _sequence >= 254 ? 1 : _sequence + 1;
            return (byte)_sequence;
        }
    }

    /// <summary>Reads the setup stored in the controller (LED count, model, firmware…).</summary>
    public StripInfo? ReadInfo(int attempts = 3, int timeoutMs = 400)
    {
        for (int i = 0; i < attempts && IsConnected; i++)
        {
            byte[]? reply = Request(StripCommand.ReadDeviceInfo, default, timeoutMs);
            if (reply is not null && StripInfo.TryParse(reply) is { } info)
            {
                Info = info;
                return info;
            }
        }

        return null;
    }

    /// <summary>Sends a command and waits for the reply with the same sequence number.</summary>
    public byte[]? Request(StripCommand command, ReadOnlySpan<byte> payload, int timeoutMs = 300)
    {
        byte sequence = NextSequence();
        byte[] packet = Packet.Command(sequence, command, payload);
        return SendAndWait(packet, sequence, (byte)command, timeoutMs);
    }

    /// <summary>
    /// Sends a command and waits briefly for its acknowledgement. Commands are paced this way so
    /// the controller is never flooded; a missing acknowledgement is not treated as an error.
    /// </summary>
    public bool Command(StripCommand command, ReadOnlySpan<byte> payload = default, int ackTimeoutMs = 120)
    {
        byte sequence = NextSequence();
        byte[] packet = Packet.Command(sequence, command, payload);
        if (ackTimeoutMs <= 0)
        {
            return WritePacket(packet);
        }

        SendAndWait(packet, sequence, (byte)command, ackTimeoutMs);
        return IsConnected;
    }

    /// <summary>
    /// Sends one screen-sync frame (a single packet, no reply expected). The frame must cover the
    /// whole strip in at most <see cref="Packet.MaxRangesPerSyncPacket"/> ranges.
    /// </summary>
    public bool SendSyncFrame(ReadOnlySpan<LedRange> ranges)
    {
        if (ranges.Length > Packet.MaxRangesPerSyncPacket)
        {
            throw new ArgumentException(
                $"A sync frame may have at most {Packet.MaxRangesPerSyncPacket} ranges; group LEDs into zones.", nameof(ranges));
        }

        return ranges.IsEmpty || WritePacket(Packet.SyncFrame(NextSequence(), ranges));
    }

    /// <summary>
    /// Splits <paramref name="ledCount"/> LEDs into at most <see cref="Packet.MaxRangesPerSyncPacket"/>
    /// contiguous zones of near-equal size (110 LEDs → 50 zones of 2–3 LEDs). Returns the first LED
    /// (0-based) of each zone plus a final end marker.
    /// </summary>
    public static int[] ZoneBoundaries(int ledCount)
    {
        int zones = Math.Clamp(ledCount, 1, Packet.MaxRangesPerSyncPacket);
        var bounds = new int[zones + 1];
        for (int z = 0; z <= zones; z++)
        {
            bounds[z] = (int)((long)z * ledCount / zones);
        }

        return bounds;
    }

    /// <summary>Win32 error or "timeout" from the most recent failed write, for diagnostics.</summary>
    public string? LastWriteError => _hid.LastError;

    /// <summary>Paints fixed colors. Splits into several packets when there are many ranges.</summary>
    public bool SetLedRanges(ReadOnlySpan<LedRange> ranges, int ackTimeoutMs = 120)
    {
        for (int start = 0; start < ranges.Length; start += Packet.MaxRangesPerCommand)
        {
            ReadOnlySpan<LedRange> chunk = ranges.Slice(start, Math.Min(Packet.MaxRangesPerCommand, ranges.Length - start));
            byte sequence = NextSequence();
            SendAndWait(Packet.LedRanges(sequence, chunk), sequence, (byte)StripCommand.SetLedRanges, ackTimeoutMs);
            if (!IsConnected)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Paints every LED one color and blanks any addresses beyond the strip.</summary>
    public bool SetStaticColor(byte r, byte g, byte b, int ledCount)
    {
        ledCount = Math.Clamp(ledCount, 1, LedRange.MaxLed);
        LedRange[] ranges = ledCount < LedRange.MaxLed
            ? [new LedRange(1, ledCount, r, g, b), new LedRange(ledCount + 1, LedRange.MaxLed, 0, 0, 0)]
            : [new LedRange(1, LedRange.MaxLed, r, g, b)];
        return SetLedRanges(ranges);
    }

    /// <summary>Hardware brightness (applies to every mode). DX Light never goes below 5.</summary>
    public bool SetBrightness(int value) => Command(StripCommand.SetBrightness, [(byte)Math.Clamp(value, 5, 255)]);

    public bool SetEffect(EffectKind kind, int index) => Command(StripCommand.SetEffect, [(byte)kind, (byte)Math.Clamp(index, 0, 255)]);

    /// <summary>0 is the fastest animation, 100 the slowest.</summary>
    public bool SetEffectSpeed(int delay) => Command(StripCommand.SetEffectSpeed, [(byte)Math.Clamp(delay, 0, 100)]);

    public bool SetMicSensitivity(int value) => Command(StripCommand.SetMicSensitivity, [(byte)Math.Clamp(value, 0, 100)]);

    /// <summary>Stops the controller from typing a web address through its keyboard interface.</summary>
    public bool SetOpenUrl(bool enabled) => Command(StripCommand.SetOpenUrl, [(byte)(enabled ? 1 : 0)]);

    /// <summary>Turns the LEDs off, using the dedicated command when the firmware has it.</summary>
    public bool TurnOff()
    {
        if (Info is null || Info.SupportsTurnOffCommand)
        {
            return Command(StripCommand.TurnOff);
        }

        return SetLedRanges([new LedRange(1, LedRange.MaxLed, 0, 0, 0)]);
    }

    private byte[]? SendAndWait(byte[] packet, byte sequence, byte command, int timeoutMs)
    {
        var pending = new PendingReply(command);
        _pending[sequence] = pending;
        try
        {
            if (!WritePacket(packet))
            {
                return null;
            }

            return pending.Reply.Wait(timeoutMs) ? pending.Reply.Result : null;
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<byte, PendingReply>(sequence, pending));
        }
    }

    private bool WritePacket(byte[] packet)
    {
        List<byte[]> reports = Packet.ToReports(packet, _hid.OutputReportLength);
        lock (_sendLock)
        {
            foreach (byte[] report in reports)
            {
                if (!_hid.Write(report))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void OnReport(byte[] report)
    {
        if (report.Length < 6)
        {
            return;
        }

        // Byte 0 is the HID report ID.
        byte[] data = report[1..];
        if (data[0] == (byte)'R' && data[1] == (byte)'B')
        {
            if (data[4] == (byte)StripCommand.SyncStatusReport)
            {
                bool on = data.Length > 6 && data[6] != 0;
                ThreadPool.QueueUserWorkItem(_ => SyncStatusReported?.Invoke(on));
                UnsolicitedReport?.Invoke(data);
                return;
            }

            if (_pending.TryGetValue(data[3], out PendingReply? pending) && pending.Command == data[4])
            {
                pending.Complete(data);
                return;
            }
        }

        UnsolicitedReport?.Invoke(data);
    }

    public void Dispose()
    {
        _hid.ReportReceived -= OnReport;
        _hid.Dispose();
    }

    private sealed class PendingReply(byte command)
    {
        private readonly TaskCompletionSource<byte[]> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte Command { get; } = command;

        public Task<byte[]> Reply => _source.Task;

        public void Complete(byte[] data) => _source.TrySetResult(data);
    }
}
