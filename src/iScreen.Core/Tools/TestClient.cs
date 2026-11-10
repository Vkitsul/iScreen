using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IScreen.Host.Net;

namespace IScreen.Host.Tools;

/// <summary>
/// Headless Windows client: speaks the real protocol, reassembles, NACKs, decodes in software and
/// reports throughput/latency. Used to validate the host without the Mac.
/// </summary>
public static unsafe class TestClient
{
    public static int Run(string host, string pin, int seconds, StreamMode mode, double lossRate, string? savePng,
        (int Width, int Height)? display = null, double blackoutAt = -1, double blackoutSeconds = 0.3,
        string? clipboardText = null, int delayMs = 0)
    {
        using var tcp = new TcpClient();
        tcp.Connect(host, Protocol.ControlPort);
        ControlChannel.ConfigureSocket(tcp.Client);
        using var channel = new ControlChannel(tcp.GetStream());

        var challenge = channel.Receive() ?? throw new IOException("closed before challenge");
        if (challenge.Type != Msg.Challenge) throw new InvalidDataException($"expected Challenge, got {challenge.Type}");
        var nonce = challenge.Payload.AsSpan(2, 16).ToArray();

        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        udp.ReceiveBufferSize = 8 * 1024 * 1024;
        udp.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.ReceiveTimeout = 2;

        var hello = new MessageWriter(Msg.Hello)
            .U16(Protocol.Version).U16((ushort)((IPEndPoint)udp.LocalEndPoint!).Port)
            .U8((byte)mode).U32(0).U16(0)
            .Bytes(Protocol.PinMac(pin, nonce));
        var (dw, dh) = display ?? (0, 0);
        hello.U16((ushort)dw).U16((ushort)dh)
            .U8((byte)((display is null ? 0 : Protocol.HelloMatchResolution) | Protocol.HelloAudio |
                       (clipboardText is null ? 0 : Protocol.HelloClipboard)));
        channel.Send(hello);

        var info = channel.Receive() ?? throw new IOException("closed before SessionInfo");
        if (info.Type == Msg.Reject) { Console.WriteLine("Rejected: wrong PIN"); return 2; }
        var r = new MessageReader(info.Payload);
        r.U16();
        uint sessionId = r.U32();
        int width = r.U16(), height = r.U16(), encW = r.U16(), encH = r.U16(), aux = r.U16();
        var layout = new FrameLayout(width, height, encW, encH, aux, (StreamMode)r.U8());
        r.U8(); r.U8();
        int fps = r.U16();
        var prim = new Primaries(r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
        float gamma = r.F32();
        int udpPort = r.U16();
        Console.WriteLine($"Session {sessionId:X8}: {layout} @{fps}fps, primaries {prim} gamma {gamma}");

        var hostIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        if (hostIp.IsIPv4MappedToIPv6) hostIp = hostIp.MapToIPv4();
        var hostVideo = new IPEndPoint(hostIp, udpPort);
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        // Control reader: pongs (RTT / clock offset), cursor messages, SessionInfo updates (monitor switch).
        var rtts = new ConcurrentQueue<double>();
        long clockOffsetUs = 0;
        int cursorShapes = 0, cursorStates = 0, sessionUpdates = 0;
        object? pendingLayout = null;
        var reader = new Thread(() =>
        {
            try
            {
                while (channel.Receive() is { } m)
                {
                    var mr = new MessageReader(m.Payload);
                    switch (m.Type)
                    {
                        case Msg.Pong:
                            long sent = (long)mr.U64(), hostNow = (long)mr.U64(), now = (long)Clock.NowUs;
                            rtts.Enqueue((now - sent) / 1000.0);
                            Interlocked.Exchange(ref clockOffsetUs, hostNow - (sent + now) / 2);
                            break;
                        case Msg.CursorShape: Interlocked.Increment(ref cursorShapes); break;
                        case Msg.CursorState: Interlocked.Increment(ref cursorStates); break;
                        case Msg.Clipboard:
                            mr.U8();
                            Console.WriteLine($"clipboard from PC: '{System.Text.Encoding.UTF8.GetString(mr.Bytes((int)mr.U32()))}'");
                            break;
                        case Msg.SessionInfo:
                            mr.U16(); mr.U32();
                            var updated = new FrameLayout(mr.U16(), mr.U16(), mr.U16(), mr.U16(), mr.U16(), (StreamMode)mr.U8());
                            Console.WriteLine($"SessionInfo update: {updated}");
                            Interlocked.Increment(ref sessionUpdates);
                            Volatile.Write(ref pendingLayout, updated);
                            break;
                    }
                }
            }
            catch (Exception) { }
        }) { IsBackground = true };
        reader.Start();

        // Decoder thread so slow software decoding doesn't stall the receive loop.
        var decodeQueue = new BlockingCollection<FrameReassembler.Frame>(64);
        int decoded = 0, decodeErrors = 0;
        byte[]? lastRgba = null;
        var decoder = new Thread(() =>
        {
            using var dec = new SoftwareDecoder();
            var rgba = Array.Empty<byte>();
            foreach (var f in decodeQueue.GetConsumingEnumerable())
            {
                bool ok = dec.Decode(f.Data.AsSpan(0, f.Size), fr =>
                {
                    decoded++;
                    var l = layout;
                    if (savePng is not null && decodeQueue.Count == 0 && fr->width == l.EncWidth && fr->height == l.EncHeight)
                    {
                        if (rgba.Length != l.Width * l.Height * 4) rgba = new byte[l.Width * l.Height * 4];
                        Yuv.ToRgba(fr->data[0], fr->linesize[0], fr->data[1], fr->linesize[1], fr->data[2], fr->linesize[2], l, rgba);
                        lastRgba = rgba;
                    }
                });
                if (!ok) decodeErrors++;
            }
        }) { IsBackground = true };
        decoder.Start();

        var reassembler = new FrameReassembler();
        // --delay-ms: hand packets over late, like a slow Wi-Fi (resends then take that long to arrive).
        var delayed = new Queue<(long DueUs, byte[] Packet)>();
        var output = new List<FrameReassembler.Frame>();
        var buffer = new byte[2048];
        var register = new byte[5];
        register[0] = Protocol.UdpRegister;
        BinaryPrimitives.WriteUInt32LittleEndian(register.AsSpan(1), sessionId);
        var random = new Random(1);
        var latencies = new List<double>();
        var hostDelays = new List<double>();
        long bytes = 0, packets = 0, dropped = 0, deliveredBefore = 0;
        long audioPackets = 0, audioGaps = 0;
        uint lastAudioSeq = 0;
        int audioRate = 0, audioPeak = 0;
        double audioLatencyMs = 0;
        long lastRegister = 0, lastPing = 0;
        var sw = Stopwatch.StartNew();

        void SendNack(uint frameId, IReadOnlyList<ushort> frags)
        {
            var nack = new byte[11 + frags.Count * 2];
            nack[0] = Protocol.UdpNack;
            BinaryPrimitives.WriteUInt32LittleEndian(nack.AsSpan(1), sessionId);
            BinaryPrimitives.WriteUInt32LittleEndian(nack.AsSpan(5), frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(nack.AsSpan(9), (ushort)frags.Count);
            for (int i = 0; i < frags.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(nack.AsSpan(11 + i * 2), frags[i]);
            udp.SendTo(nack, hostVideo);
        }

        void Collect()
        {
            foreach (var f in output)
            {
                long offset = Interlocked.Read(ref clockOffsetUs);
                latencies.Add((f.CompletedUs + offset - (long)f.CaptureUs) / 1000.0);
                hostDelays.Add(f.HostDelay100us / 10.0);
                decodeQueue.TryAdd(f);
            }
            output.Clear();
        }

        while (!stop.IsCancellationRequested)
        {
            if (Interlocked.Exchange(ref pendingLayout, null) is FrameLayout switched)
            {
                // Same as the Mac client: new monitor, start over from a fresh keyframe.
                layout = switched;
                deliveredBefore += reassembler.Delivered;
                reassembler = new FrameReassembler();
                channel.TrySend(new MessageWriter(Msg.KeyframeRequest));
            }
            long ms = sw.ElapsedMilliseconds;
            if (ms - lastRegister >= 500) { udp.SendTo(register, hostVideo); lastRegister = ms; }
            if (ms - lastPing >= 1000) { channel.TrySend(new MessageWriter(Msg.Ping).U64(Clock.NowUs)); lastPing = ms; }
            if (clipboardText is not null && ms >= 1000)
            {
                // As if copied on the Mac.
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(clipboardText);
                channel.TrySend(new MessageWriter(Msg.Clipboard).U8(1).U32((uint)utf8.Length).Bytes(utf8));
                clipboardText = null;
            }

            try
            {
                int n = udp.Receive(buffer);
                packets++;
                bytes += n;
                if (lossRate > 0 && random.NextDouble() < lossRate) { dropped++; continue; }
                // Blackout: lose everything for a while, like a Wi-Fi hiccup (past ~1 s NACKs can no longer repair it).
                if (blackoutAt >= 0 && sw.Elapsed.TotalSeconds is var t && t >= blackoutAt && t < blackoutAt + blackoutSeconds)
                {
                    dropped++;
                    continue;
                }
                if (buffer[0] == Protocol.UdpAudio && n >= Protocol.AudioHeaderSize)
                {
                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
                    if (audioPackets > 0 && seq != lastAudioSeq + 1) audioGaps++;
                    lastAudioSeq = seq;
                    audioRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8));
                    ulong captured = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(12));
                    audioLatencyMs += ((long)Clock.NowUs + Interlocked.Read(ref clockOffsetUs) - (long)captured) / 1000.0;
                    for (int s = Protocol.AudioHeaderSize; s + 1 < n; s += 2)
                        audioPeak = Math.Max(audioPeak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(s))));
                    audioPackets++;
                    continue;
                }
                if (delayMs > 0) delayed.Enqueue(((long)Clock.NowUs + delayMs * 1000L, buffer.AsSpan(0, n).ToArray()));
                else reassembler.OnPacket(buffer.AsSpan(0, n), (long)Clock.NowUs, output);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut) { }

            while (delayed.TryPeek(out var d) && d.DueUs <= (long)Clock.NowUs)
                reassembler.OnPacket(delayed.Dequeue().Packet, (long)Clock.NowUs, output);
            reassembler.Tick((long)Clock.NowUs, SendNack,
                () => channel.TrySend(new MessageWriter(Msg.KeyframeRequest)), output);
            Collect();
        }
        decodeQueue.CompleteAdding();
        decoder.Join(10_000);

        double secs = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{secs:0.0}s: {packets} packets ({dropped} dropped on purpose), {bytes * 8 / secs / 1e6:0.0} Mbps");
        long delivered = deliveredBefore + reassembler.Delivered;
        Console.WriteLine($"frames delivered {delivered} ({delivered / secs:0.0} fps), decoded {decoded}, decode errors {decodeErrors}");
        if (sessionUpdates > 0) Console.WriteLine($"monitor switches received: {sessionUpdates}, now {layout.Width}x{layout.Height}");
        Console.WriteLine($"frames dropped {reassembler.Dropped}, repaired by FEC {reassembler.RepairedByFec}, NACKs {reassembler.NacksSent}, keyframe requests {reassembler.KeyframeRequests}");
        if (latencies.Count > 0)
        {
            latencies.Sort();
            Console.WriteLine($"capture -> frame received: p50 {latencies[latencies.Count / 2]:0.0} ms, p95 {latencies[latencies.Count * 95 / 100]:0.0} ms, max {latencies[^1]:0.0} ms");
            Console.WriteLine($"  of which on host (capture -> sent): avg {hostDelays.Average():0.0} ms");
        }
        if (!rtts.IsEmpty) Console.WriteLine($"TCP RTT avg {rtts.Average():0.00} ms");
        Console.WriteLine($"cursor messages: {cursorShapes} shapes, {cursorStates} positions");
        Console.WriteLine(audioPackets == 0
            ? "audio: no packets (nothing playing on the PC, or capture failed: see the host output)"
            : $"audio: {audioPackets} packets ({audioPackets / secs:0} /s), {audioRate} Hz, sequence gaps {audioGaps}, " +
              $"peak level {audioPeak * 100 / 32767}%, capture -> received avg {audioLatencyMs / audioPackets:0.0} ms");
        if (savePng is not null && lastRgba is not null)
        {
            Png.Write(savePng, lastRgba, layout.Width, layout.Height);
            Console.WriteLine($"Saved last frame to {savePng}");
        }
        return 0;
    }
}
