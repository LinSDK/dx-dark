using System.Diagnostics;
using DxDark.Capture;
using DxDark.Core.Platform;
using DxDark.Protocol;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?")
        {
            PrintHelp();
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];
        try
        {
            return command switch
            {
                "list" => List(),
                "monitors" => Monitors(),
                "capture-test" => CaptureTest(rest),
                _ => WithStrip(command, rest),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            dxdark-cli — test tool for DX Light screen-sync strips (offline, no network)

              list                         list connected strips (USB HID interfaces)
              info                         read the setup stored in the controller
              color <r> <g> <b>            static color (0-255 each)
              off                          turn the LEDs off
              brightness <5-255>           hardware brightness
              effect <1-7>                 built-in animated effect
              rhythm <1-7>                 built-in music effect (controller microphone)
              speed <0-100>                effect speed (0 = fastest)
              sensitivity <0-100>          microphone sensitivity
              openurl on|off               the controller's "type a web address" feature
              chase [ms]                   light one LED at a time to find LED numbers
              listen [seconds] [sync]      print everything the controller sends (press its buttons);
                                           "sync" streams a blue sync pattern meanwhile
              bench [seconds] [fps]        moving rainbow as sync frames; fps 0 = as fast as possible
              monitors                     list monitors
              capture-test [seconds] [n]   measure screen capture speed on monitor n

            Sync frames always use one packet of at most 50 color zones: larger packets hang
            firmware 1.8.2, and frames split over several packets flicker.
            """);
    }

    private static int List()
    {
        IReadOnlyList<DxDark.Protocol.Hid.HidDeviceInfo> devices = LightStrip.FindDevices();
        if (devices.Count == 0)
        {
            Console.WriteLine("No strip found.");
            return 2;
        }

        foreach (var d in devices)
        {
            Console.WriteLine($"{d.VendorId:X4}:{d.ProductId:X4}  usage page 0x{d.UsagePage:X4}  in {d.InputReportLength} / out {d.OutputReportLength} bytes  {d.Manufacturer} {d.Product}");
            Console.WriteLine($"  {d.Path}");
        }

        return 0;
    }

    private static int WithStrip(string command, string[] args)
    {
        using LightStrip strip = LightStrip.OpenFirst() ?? throw new InvalidOperationException("No strip found. Is it plugged in?");
        StripInfo info = strip.ReadInfo() ?? throw new InvalidOperationException("The strip did not answer the device-info request.");

        switch (command)
        {
            case "info":
                Console.WriteLine($"Model ID      {info.ModelId}");
                Console.WriteLine($"Firmware      {info.Firmware}");
                Console.WriteLine($"LED count     {info.LedCount}");
                Console.WriteLine($"Display size  {info.DisplaySizeInches}\"");
                Console.WriteLine($"UUID          {info.Uuid}");
                Console.WriteLine($"Flags         {info.Flags}");
                return 0;

            case "color":
                byte r = ParseByte(args, 0), g = ParseByte(args, 1), b = ParseByte(args, 2);
                strip.SetStaticColor(r, g, b, info.LedCount);
                return 0;

            case "off":
                strip.TurnOff();
                return 0;

            case "brightness":
                strip.SetBrightness(ParseInt(args, 0, 5, 255));
                return 0;

            case "effect":
                strip.SetLedRanges([new LedRange(1, LedRange.MaxLed, 0, 0, 0)]);
                strip.SetEffect(EffectKind.Dynamic, ParseInt(args, 0, 1, 7) - 1);
                return 0;

            case "rhythm":
                strip.SetLedRanges([new LedRange(1, LedRange.MaxLed, 0, 0, 0)]);
                strip.SetEffect(EffectKind.Rhythm, ParseInt(args, 0, 1, 7) - 1);
                return 0;

            case "speed":
                strip.SetEffectSpeed(ParseInt(args, 0, 0, 100));
                return 0;

            case "sensitivity":
                strip.SetMicSensitivity(ParseInt(args, 0, 0, 100));
                return 0;

            case "openurl":
                strip.SetOpenUrl(args.Length > 0 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase));
                return 0;

            case "chase":
                return Chase(strip, info, args.Length > 0 ? ParseInt(args, 0, 20, 5000) : 300);

            case "listen":
                return Listen(strip, info, args.Length > 0 ? ParseInt(args, 0, 1, 3600) : 30, args.Length > 1 && args[1] == "sync");

            case "bench":
                return Bench(strip, info, args.Length > 0 ? ParseInt(args, 0, 1, 120) : 5, args.Length > 1 ? ParseInt(args, 1, 0, 240) : 60);

            default:
                Console.Error.WriteLine($"Unknown command '{command}'. Run without arguments for help.");
                return 1;
        }
    }

    private static int Chase(LightStrip strip, StripInfo info, int stepMs)
    {
        Console.WriteLine("Lighting LEDs one at a time. Press Ctrl+C to stop.");
        for (int led = 1; led <= info.LedCount; led++)
        {
            var ranges = new List<LedRange>();
            if (led > 1)
            {
                ranges.Add(new LedRange(1, led - 1, 0, 0, 0));
            }

            ranges.Add(LedRange.Single(led, 0, 255, 0));
            if (led < LedRange.MaxLed)
            {
                ranges.Add(new LedRange(led + 1, LedRange.MaxLed, 0, 0, 0));
            }

            strip.SetLedRanges(ranges.ToArray());
            Console.Write($"\rLED {led,3} / {info.LedCount}");
            Thread.Sleep(stepMs);
        }

        Console.WriteLine();
        strip.TurnOff();
        return 0;
    }

    /// <summary>
    /// Prints everything the controller sends. With <paramref name="sync"/>, streams a slow blue
    /// pattern like screen sync does, pausing whenever the controller reports sync switched off.
    /// </summary>
    private static int Listen(LightStrip strip, StripInfo info, int seconds, bool sync)
    {
        Console.WriteLine($"Listening for {seconds} s{(sync ? " while streaming sync frames" : "")} — press the controller's buttons now.");
        var sw = Stopwatch.StartNew();
        bool streaming = sync;
        strip.UnsolicitedReport += data =>
        {
            int length = Math.Min(data.Length, 32);
            Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:0.000}s] {Convert.ToHexString(data, 0, length)}");
        };
        strip.SyncStatusReported += on =>
        {
            Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:0.000}s] sync status report: {(on ? "ON" : "OFF")}");
            if (sync)
            {
                streaming = on;
            }
        };

        int[] zones = LightStrip.ZoneBoundaries(info.LedCount);
        var ranges = new LedRange[zones.Length - 1];
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (streaming)
            {
                double t = sw.Elapsed.TotalSeconds;
                for (int z = 0; z < ranges.Length; z++)
                {
                    double wave = 0.5 + 0.5 * Math.Sin(z * 0.3 + t * 2);
                    ranges[z] = new LedRange(zones[z] + 1, zones[z + 1], 0, (byte)(30 * wave), (byte)(60 + 120 * wave));
                }

                strip.SendSyncFrame(ranges);
            }

            Thread.Sleep(33);
        }

        if (sync)
        {
            strip.TurnOff();
        }

        return 0;
    }

    /// <summary>Streams a moving rainbow as 50-zone sync frames and reports the achieved rate.</summary>
    private static int Bench(LightStrip strip, StripInfo info, int seconds, int fps)
    {
        using var sleeper = new PrecisionSleeper();
        int[] zones = LightStrip.ZoneBoundaries(info.LedCount);
        var ranges = new LedRange[zones.Length - 1];
        int bytes = 7 + ranges.Length * 5;
        Console.WriteLine($"{info.LedCount} LEDs in {ranges.Length} zones → {bytes}-byte frame, {(bytes + Packet.ReportDataLength - 1) / Packet.ReportDataLength} USB reports; target {(fps == 0 ? "max" : fps.ToString())} fps for {seconds} s…");

        int replies = 0;
        strip.UnsolicitedReport += _ => Interlocked.Increment(ref replies);

        var sw = Stopwatch.StartNew();
        int frames = 0, slow = 0, worstFrame = 0;
        double worstMs = 0, deadline = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double t = sw.Elapsed.TotalSeconds;
            for (int z = 0; z < ranges.Length; z++)
            {
                (byte r, byte g, byte b) = Rainbow(((zones[z] + zones[z + 1]) / 2.0 / info.LedCount + t * 0.25) % 1.0);
                ranges[z] = new LedRange(zones[z] + 1, zones[z + 1], r, g, b);
            }

            long start = Stopwatch.GetTimestamp();
            if (!strip.SendSyncFrame(ranges))
            {
                Console.Error.WriteLine($"Write failed after {frames} frames: {strip.LastWriteError}");
                return 3;
            }

            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (ms > worstMs)
            {
                worstMs = ms;
                worstFrame = frames;
            }

            slow += ms > 50 ? 1 : 0;
            frames++;
            if (fps > 0)
            {
                deadline = Math.Max(deadline + 1000.0 / fps, sw.Elapsed.TotalMilliseconds);
                sleeper.Sleep(deadline - sw.Elapsed.TotalMilliseconds);
            }
        }

        double elapsed = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{frames} frames in {elapsed:0.00} s → {frames / elapsed:0.0} frames/s");
        Console.WriteLine($"Slowest send {worstMs:0.0} ms (frame #{worstFrame}), {slow} frame(s) over 50 ms, {replies} replies");

        Thread.Sleep(100);
        bool healthy = strip.ReadInfo(attempts: 2) is not null;
        Console.WriteLine(healthy ? "Strip still responding: yes" : "Strip still responding: NO — power-cycle it");
        if (healthy)
        {
            strip.TurnOff();
        }

        return healthy ? 0 : 4;
    }

    /// <summary>Hue to RGB with R+G+B ≤ 255, the same per-LED power cap DX Light uses.</summary>
    private static (byte, byte, byte) Rainbow(double hue)
    {
        double r = Math.Clamp(Math.Abs(hue * 6 - 3) - 1, 0, 1);
        double g = Math.Clamp(2 - Math.Abs(hue * 6 - 2), 0, 1);
        double b = Math.Clamp(2 - Math.Abs(hue * 6 - 4), 0, 1);
        double scale = 255 / Math.Max(1, r + g + b);
        return ((byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
    }

    private static int Monitors()
    {
        foreach (MonitorInfo m in MonitorEnumerator.List())
        {
            Console.WriteLine($"{m.Label}   [{m.DeviceName}, at {m.Left},{m.Top}, rotation {m.Rotation}, {m.AdapterName}]");
        }

        return 0;
    }

    private static int CaptureTest(string[] args)
    {
        int seconds = args.Length > 0 ? ParseInt(args, 0, 1, 120) : 5;
        List<MonitorInfo> monitors = MonitorEnumerator.List();
        string? device = args.Length > 1 ? monitors[ParseInt(args, 1, 1, monitors.Count) - 1].DeviceName : null;

        using var capturer = new ScreenCapturer(device);
        var frame = new CapturedFrame();
        var sw = Stopwatch.StartNew();
        int frames = 0, unchanged = 0, unavailable = 0;
        double captureMs = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            CaptureStatus status = capturer.TryCapture(frame, 0);
            switch (status)
            {
                case CaptureStatus.NewFrame:
                    frames++;
                    break;
                case CaptureStatus.NoChange:
                    unchanged++;
                    break;
                default:
                    unavailable++;
                    Thread.Sleep(100);
                    break;
            }

            if (status == CaptureStatus.NewFrame)
            {
                // Time a second, immediate capture of the same frame's processing cost.
                long start = Stopwatch.GetTimestamp();
                capturer.TryCapture(frame, 0);
                captureMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Thread.Sleep(5);
        }

        Console.WriteLine($"Monitor: {capturer.Monitor?.Label ?? "?"}");
        Console.WriteLine($"Analysis image: {frame.Width}×{frame.Height} (from {frame.SourceWidth}×{frame.SourceHeight})");
        Console.WriteLine($"{frames} new frames in {seconds} s ({frames / (double)seconds:0.0}/s), {unchanged} polls without change, {unavailable} unavailable");
        if (frames > 0)
        {
            Console.WriteLine($"Average capture call: {captureMs / frames:0.00} ms");
        }

        if (capturer.LastError is not null)
        {
            Console.WriteLine($"Last error: {capturer.LastError}");
        }

        return 0;
    }

    private static int ParseInt(string[] args, int index, int min, int max)
    {
        if (index >= args.Length || !int.TryParse(args[index], out int value) || value < min || value > max)
        {
            throw new ArgumentException($"Expected a number from {min} to {max} as argument {index + 1}.");
        }

        return value;
    }

    private static byte ParseByte(string[] args, int index) => (byte)ParseInt(args, index, 0, 255);
}
