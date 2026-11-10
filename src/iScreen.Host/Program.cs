using System.Globalization;
using IScreen.Host;
using IScreen.Host.Capture;
using IScreen.Host.Encode;
using IScreen.Host.Streaming;
using IScreen.Host.Tools;

Console.OutputEncoding = System.Text.Encoding.UTF8;
HostProcess.Initialize();

var opts = Options.Parse(args);
switch (opts.Command)
{
    case "list":
    {
        var monitors = Monitors.Enumerate();
        foreach (var m in monitors) Console.WriteLine(m.Info);
        Monitors.DisposeAll(monitors);
        return 0;
    }
    case "modes":
    {
        foreach (var m in Monitors.List())
        {
            var current = DisplayModes.Current(m.DeviceName);
            Console.WriteLine($"[{m.Index}] {m.FriendlyName} now {current}, saved {DisplayModes.Saved(m.DeviceName)}, " +
                              $"desktop rect {DisplayModes.CurrentRect(m.DeviceName)}");
            Console.WriteLine("    " + string.Join("  ", DisplayModes.List(m.DeviceName)
                .Where(d => d.Orientation == current?.Orientation)
                .Select(d => $"{d.Width}x{d.Height}@{d.Frequency}({d.Aspect:0.000})")));
            if (opts.Get("client") is { } client && client.Split('x') is [var w, var h])
                Console.WriteLine($"    best for {client}: {DisplayModes.BestFor(m.DeviceName, int.Parse(w), int.Parse(h))?.ToString() ?? "keep current"}");
        }
        return 0;
    }
    case "fectest":
        return FecTest.Run(opts.Get("vector"));
    case "serve":
        return Serve(opts);
    case "selftest":
        return SelfTest.Run(opts.Monitor, opts.Has("screen"), opts.Int("frames", 30), opts.Get("save"), opts.Encoder(), opts.Has("paced"));
    case "testclient":
        return TestClient.Run(opts.Get("host") ?? "127.0.0.1", opts.Get("pin") ?? HostConfig.LoadOrCreate().Pin,
            opts.Int("seconds", 5), opts.Mode,
            double.Parse(opts.Get("loss") ?? "0", CultureInfo.InvariantCulture), opts.Get("save"),
            opts.Get("display")?.Split('x') is [var dw, var dh] ? (int.Parse(dw), int.Parse(dh)) : null,
            double.Parse(opts.Get("blackout") ?? "-1", CultureInfo.InvariantCulture),
            double.Parse(opts.Get("blackout-seconds") ?? "0.3", CultureInfo.InvariantCulture),
            opts.Get("clipboard"), opts.Int("delay-ms", 0));
    default:
        Console.WriteLine("""
            iscreen-host <command> [options]
              list                         monitors with colour primaries
              serve [--monitor N] [--mode 444|420] [--pattern] [--no-input] [--bind IP] [--seconds N]
                                           stream to a client (prints the pairing PIN)
              selftest [--monitor N] [--screen] [--frames 30] [--save DIR] [--paced]
                                           encode/decode round trip, 4:2:0 vs 4:4:4 quality
              testclient [--host IP] [--pin PIN] [--seconds 5] [--mode 444|420] [--loss 0.01] [--save F.png]
                                           headless client for testing the host
            encoder options: --bitrate MBPS --fps N --quality speed|balanced|quality --rc vbr_latency|cbr|...
                             --codec hevc_amf --opt name=value,...
            """);
        return opts.Command is null or "help" ? 0 : 1;
}

static int Serve(Options opts)
{
    var config = HostConfig.LoadOrCreate();
    var defaults = new SessionOptions(opts.Mode, opts.Encoder(), opts.Has("pattern"));
    Console.WriteLine($"iScreen host - PIN {config.Pin}{(defaults.Pattern ? " - streaming a test pattern" : "")}");

    var bind = System.Net.IPAddress.Parse(opts.Get("bind") ?? "0.0.0.0");
    using var server = new ControlServer(config, opts.Monitor, defaults, allowInput: !opts.Has("no-input"), bind);
    server.GpuLost += reason => Console.WriteLine($"GPU lost ({reason}): restart iscreen-host to recover");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    server.Sender.FecPercent = opts.Int("fec", server.Sender.FecPercent);
    if (opts.Int("seconds", 0) is > 0 and var seconds) cts.CancelAfter(TimeSpan.FromSeconds(seconds));

    var reporter = new Thread(() =>
    {
        long lastFrames = 0, lastBytes = 0, lastEncode = 0, lastDelay = 0;
        while (!cts.Token.WaitHandle.WaitOne(5000))
        {
            if (server.Session is not { } s) { lastFrames = lastBytes = lastEncode = lastDelay = 0; continue; }
            long frames = s.FramesSent, bytes = server.Sender.BytesSent, enc = s.EncodeUsTotal, delay = s.HostDelayUsTotal;
            long df = frames - lastFrames;
            if (df > 0)
                Console.WriteLine($"  {df / 5.0:0.0} fps, {(bytes - lastBytes) * 8 / 5e6:0.0} Mbps, " +
                                  $"encode {(enc - lastEncode) / 1000.0 / df:0.0} ms, capture->sent {(delay - lastDelay) / 1000.0 / df:0.0} ms, " +
                                  $"resent {server.Sender.PacketsResent} pkts, keyframes {s.Keyframes}");
            (lastFrames, lastBytes, lastEncode, lastDelay) = (frames, bytes, enc, delay);
        }
    }) { IsBackground = true };
    reporter.Start();

    // Typing a monitor number + Enter switches the stream live; "fail" tests recovery from a stalled encoder.
    new Thread(() =>
    {
        while (Console.ReadLine() is { } line)
        {
            // Digits only: a caller's BOM can arrive as stray characters in the legacy code page.
            if (line.Trim().EndsWith("fail"))
            {
                Console.WriteLine("simulating an encoder stall");
                server.Session?.SimulateFailure();
            }
            else if (int.TryParse(new string(line.Where(char.IsAsciiDigit).ToArray()), out int index))
            {
                Console.WriteLine($"switching to monitor {index}");
                server.SetMonitor(index);
            }
            else if (line.Length > 0) Console.WriteLine($"stdin: type a monitor number (got '{line}')");
        }
    }) { IsBackground = true }.Start();

    server.Run(cts.Token);
    return 0;
}

sealed class Options
{
    readonly Dictionary<string, string?> _values = new();
    public string? Command { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string key = args[i][2..];
                string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : null;
                o._values[key] = value;
            }
            else o.Command ??= args[i];
        }
        return o;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.GetValueOrDefault(key);
    public int Int(string key, int fallback) => int.TryParse(Get(key), out int v) ? v : fallback;
    public int Monitor => Int("monitor", 0);
    public StreamMode Mode => Get("mode") == "420" ? StreamMode.Yuv420 : StreamMode.Yuv444Packed;

    public EncoderSettings Encoder()
    {
        var s = new EncoderSettings();
        return s with
        {
            Fps = Int("fps", s.Fps),
            BitrateKbps = Int("bitrate", s.BitrateKbps / 1000) * 1000,
            Quality = Get("quality") ?? s.Quality,
            RateControl = Get("rc") ?? s.RateControl,
            Codec = Get("codec") ?? s.Codec,
            Extra = Get("opt")?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
        };
    }
}
