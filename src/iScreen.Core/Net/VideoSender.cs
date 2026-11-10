using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace IScreen.Host.Net;

/// <summary>
/// Sends encoded frames as UDP shards: the data, then Reed-Solomon parity (FecPercent) so the client can
/// rebuild lost packets without asking. Answers NACKs for data shards from a cache of recent frames.
/// The client endpoint is learned from its Register packets.
/// </summary>
public sealed class VideoSender : IDisposable
{
    sealed class CachedFrame
    {
        public uint FrameId = uint.MaxValue;
        public byte[] Data = new byte[256 * 1024]; // shard-aligned, zero padded
        public int Length, DataShards, ParityShards, FecPercent;
        public byte Flags;
        public ulong CaptureUs;
        public ushort HostDelay;
        public long[] LastResend = new long[256]; // Stopwatch ticks, per data shard
        public long LastWholeResend;
    }

    const int CacheSize = 64;
    const int S = Protocol.MaxPayload;

    readonly Socket _socket;
    readonly CachedFrame[] _cache = Enumerable.Range(0, CacheSize).Select(_ => new CachedFrame()).ToArray();
    readonly Lock _cacheLock = new();
    readonly byte[] _sendBuffer = new byte[Protocol.VideoHeaderSize + S];
    readonly byte[] _resendBuffer = new byte[Protocol.VideoHeaderSize + S];
    readonly List<byte[]> _parity = new();
    readonly Thread _receiveThread;
    volatile bool _disposed;
    volatile SocketAddress? _client;
    IPEndPoint? _clientEndPoint;

    public VideoSender(IPAddress bindAddress, int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Without this an ICMP "port unreachable" makes the next ReceiveFrom throw WSAECONNRESET.
        _socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        _socket.SendBufferSize = 8 * 1024 * 1024;
        _socket.ReceiveBufferSize = 1024 * 1024;
        _socket.Bind(new IPEndPoint(bindAddress, port));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "udp-receive" };
        _receiveThread.Start();
    }

    public int Port { get; }
    public uint SessionId { get; set; }
    public bool HasClient => _client is not null;
    public long BytesSent;
    public long PacketsResent;
    public long ResendsRefused;

    /// <summary>
    /// Ceiling for retransmissions. On a congested Wi-Fi a client may keep asking for the same packets;
    /// answering all of it at line rate is what turned losses into a meltdown (600+ Mbps of resends).
    /// </summary>
    public int ResendMbps { get; set; } = 40;
    const int ResendBurstBytes = 64 * (Protocol.VideoHeaderSize + S);
    static readonly long DuplicateWindow = Stopwatch.Frequency * 30 / 1000;  // same shard: once per 30 ms
    static readonly long WholeFrameWindow = Stopwatch.Frequency * 50 / 1000; // whole frame: once per 50 ms
    double _resendTokens = ResendBurstBytes;
    long _resendRefilledAt = Stopwatch.GetTimestamp();

    /// <summary>Parity as a percentage of the data shards (per block, at least one). 0 = no FEC.</summary>
    public int FecPercent { get; set; } = 20;

    /// <summary>Send rate for large frames; 0 = as fast as possible.</summary>
    public int PacingMbps { get; set; } = 250;
    const int PacingBurst = 8;

    /// <summary>Raised when a client registers (or moves to a new endpoint): send it a keyframe.</summary>
    public event Action<IPEndPoint>? ClientRegistered;

    public void ResetClient()
    {
        _client = null;
        _clientEndPoint = null;
    }

    public void SendFrame(uint frameId, ReadOnlySpan<byte> accessUnit, bool keyframe, ulong captureUs, ushort hostDelay100us)
    {
        var client = _client;
        int dataShards = Math.Max(1, (accessUnit.Length + S - 1) / S);
        var fec = FecPercent > 0 ? new FecLayout(dataShards, FecPercent, Protocol.FecBlockData) : null;
        CachedFrame slot;
        lock (_cacheLock)
        {
            slot = _cache[frameId % CacheSize];
            int padded = dataShards * S;
            if (slot.Data.Length < padded) slot.Data = new byte[padded * 3 / 2];
            accessUnit.CopyTo(slot.Data);
            slot.Data.AsSpan(accessUnit.Length, padded - accessUnit.Length).Clear(); // RS needs whole shards
            slot.FrameId = frameId;
            slot.Length = accessUnit.Length;
            slot.DataShards = dataShards;
            slot.ParityShards = fec?.ParityShards ?? 0;
            slot.FecPercent = fec is null ? 0 : FecPercent;
            slot.Flags = keyframe ? Protocol.FlagKeyframe : (byte)0;
            slot.CaptureUs = captureUs;
            slot.HostDelay = hostDelay100us;
            if (slot.LastResend.Length < dataShards) slot.LastResend = new long[dataShards * 2];
            Array.Clear(slot.LastResend, 0, dataShards);
            slot.LastWholeResend = 0;
        }
        if (client is null) return;

        if (fec is not null) ComputeParity(slot, fec);

        // Order: all data, then parity round-robin over the blocks. Pacing: a big frame sent at line rate
        // (1 Gbps from a wired PC) overflows the Wi-Fi access point's queue; small bursts at PacingMbps don't.
        int total = dataShards + slot.ParityShards;
        long start = Stopwatch.GetTimestamp();
        double ticksPerByte = PacingMbps > 0 ? Stopwatch.Frequency / (PacingMbps * 1_000_000 / 8.0) : 0;
        long sent = 0;
        int n = 0;
        foreach (int shard in SendOrder(dataShards, fec))
        {
            if (ticksPerByte > 0 && n >= PacingBurst && n % PacingBurst == 0)
            {
                long due = start + (long)(sent * ticksPerByte);
                while (Stopwatch.GetTimestamp() < due) Thread.SpinWait(32);
            }
            SendShard(_sendBuffer, client, slot, shard);
            sent += Protocol.VideoHeaderSize + S;
            n++;
        }
        Debug.Assert(n == total);
    }

    static IEnumerable<int> SendOrder(int dataShards, FecLayout? fec)
    {
        for (int i = 0; i < dataShards; i++) yield return i;
        if (fec is null) yield break;
        int rounds = Enumerable.Range(0, fec.Blocks).Max(fec.ParityCount);
        for (int j = 0; j < rounds; j++)
            for (int b = 0; b < fec.Blocks; b++)
                if (j < fec.ParityCount(b)) yield return fec.ParityShard(b, j);
    }

    void ComputeParity(CachedFrame slot, FecLayout fec)
    {
        while (_parity.Count < fec.ParityShards) _parity.Add(new byte[S]);
        for (int b = 0; b < fec.Blocks; b++)
        {
            var data = new Memory<byte>[fec.DataInBlock(b)];
            for (int n = 0; n < data.Length; n++) data[n] = slot.Data.AsMemory(fec.DataShard(b, n) * S, S);
            var parity = new Memory<byte>[fec.ParityCount(b)];
            for (int j = 0; j < parity.Length; j++) parity[j] = _parity[fec.ParityShard(b, j) - fec.DataShards];
            ReedSolomon.Encode(data, parity);
        }
    }

    /// <summary>Audio goes over the same socket; never cached or retransmitted.</summary>
    public void SendAudio(ReadOnlySpan<byte> packet)
    {
        var client = _client;
        if (client is null) return;
        try
        {
            _socket.SendTo(packet, SocketFlags.None, client);
            Interlocked.Add(ref BytesSent, packet.Length);
        }
        catch (SocketException) { }
    }

    /// <summary>Header v2 (28 bytes) + one shard. Data shards come from the cache, parity from _parity.</summary>
    void SendShard(byte[] buffer, SocketAddress client, CachedFrame frame, int shard)
    {
        var h = buffer.AsSpan();
        h[0] = Protocol.UdpVideo;
        h[1] = frame.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(h[2..], (ushort)shard);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], (ushort)frame.DataShards);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], frame.HostDelay);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], frame.FrameId);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)frame.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(h[16..], frame.CaptureUs);
        BinaryPrimitives.WriteUInt16LittleEndian(h[24..], (ushort)frame.ParityShards);
        h[26] = (byte)frame.FecPercent;
        h[27] = Protocol.FecBlockData;

        int size;
        if (shard < frame.DataShards)
        {
            int offset = shard * S;
            size = Math.Min(S, frame.Length - offset); // the last data shard goes out unpadded
            frame.Data.AsSpan(offset, size).CopyTo(h[Protocol.VideoHeaderSize..]);
        }
        else
        {
            size = S;
            _parity[shard - frame.DataShards].CopyTo(h[Protocol.VideoHeaderSize..]);
        }
        try
        {
            _socket.SendTo(buffer.AsSpan(0, Protocol.VideoHeaderSize + size), SocketFlags.None, client);
            Interlocked.Add(ref BytesSent, Protocol.VideoHeaderSize + size);
        }
        catch (SocketException) { /* transient (buffer full / no route); the client will NACK */ }
    }

    void ReceiveLoop()
    {
        var buffer = new byte[2048];
        var from = new SocketAddress(AddressFamily.InterNetwork);
        while (!_disposed)
        {
            int n;
            try { n = _socket.ReceiveFrom(buffer, SocketFlags.None, from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }
            if (n < 5) continue;
            uint session = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(1));
            if (session != SessionId || SessionId == 0) continue;

            switch (buffer[0])
            {
                case Protocol.UdpRegister:
                    if (_client is null || !_client.Equals(from))
                    {
                        var ep = (IPEndPoint)new IPEndPoint(IPAddress.Any, 0).Create(from);
                        _clientEndPoint = ep;
                        var copy = new SocketAddress(from.Family, from.Size);
                        from.Buffer.Span[..from.Size].CopyTo(copy.Buffer.Span);
                        _client = copy;
                        ClientRegistered?.Invoke(ep);
                    }
                    break;
                case Protocol.UdpNack when n >= 11:
                    HandleNack(buffer.AsSpan(0, n));
                    break;
            }
        }
    }

    /// <summary>
    /// Resends data shards (parity is not kept: the client only asks for what FEC can't rebuild),
    /// skipping duplicates of a recent resend and staying within the resend budget.
    /// </summary>
    void HandleNack(ReadOnlySpan<byte> p)
    {
        var client = _client;
        if (client is null) return;
        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(p[5..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(p[9..]);
        long now = Stopwatch.GetTimestamp();
        lock (_cacheLock)
        {
            var slot = _cache[frameId % CacheSize];
            if (slot.FrameId != frameId) return; // too old
            if (count == 0) // whole frame lost: the client doesn't know its shard count
            {
                if (now - slot.LastWholeResend < WholeFrameWindow) return;
                slot.LastWholeResend = now;
                for (int i = 0; i < slot.DataShards; i++) Resend(client, slot, i, now);
                return;
            }
            for (int i = 0; i < count && 11 + i * 2 + 2 <= p.Length; i++)
            {
                int index = BinaryPrimitives.ReadUInt16LittleEndian(p[(11 + i * 2)..]);
                if (index < slot.DataShards) Resend(client, slot, index, now);
            }
        }
    }

    void Resend(SocketAddress client, CachedFrame slot, int shard, long now)
    {
        if (slot.LastResend[shard] != 0 && now - slot.LastResend[shard] < DuplicateWindow) return;
        // Token bucket: ResendMbps sustained, small bursts allowed.
        _resendTokens = Math.Min(ResendBurstBytes,
            _resendTokens + (now - _resendRefilledAt) * (ResendMbps * 125_000.0) / Stopwatch.Frequency);
        _resendRefilledAt = now;
        const int Cost = Protocol.VideoHeaderSize + S;
        if (_resendTokens < Cost)
        {
            ResendsRefused++; // the client will ask again or resynchronise on a keyframe
            return;
        }
        _resendTokens -= Cost;
        slot.LastResend[shard] = now;
        SendShard(_resendBuffer, client, slot, shard);
        Interlocked.Increment(ref PacketsResent);
    }

    public override string ToString() => _clientEndPoint?.ToString() ?? "(no client)";

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }
}
