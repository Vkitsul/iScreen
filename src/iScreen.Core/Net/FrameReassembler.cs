using System.Buffers.Binary;

namespace IScreen.Host.Net;

/// <summary>
/// Client-side reassembly of UDP video shards into access units, delivered strictly in order.
/// Lost data shards are first rebuilt from Reed-Solomon parity (no round trip); only what FEC can't
/// recover is NACKed, and a gap that can't be repaired quickly triggers a keyframe request.
/// The Mac client mirrors this logic (Reassembler.swift).
/// </summary>
public sealed class FrameReassembler
{
    public readonly record struct Frame(uint FrameId, byte[] Data, int Size, bool Key, ulong CaptureUs, ushort HostDelay100us, long CompletedUs);

    const int S = Protocol.MaxPayload;

    sealed class Pending
    {
        public uint FrameId;
        public byte[] Data = [];          // DataShards * S, zero padded
        public byte[]?[] Parity = [];     // allocated as parity shards arrive
        public int Size, DataShards, ParityShards, HaveData;
        public bool[] Have = [];          // DataShards + ParityShards
        public FecLayout? Fec;
        public bool Key, Repaired;
        public ulong CaptureUs;
        public ushort HostDelay;
        public long LastPacketUs, LastNackUs;
        public int Nacks;
        public bool Complete => HaveData == DataShards;
    }

    // NACK pacing. Re-asking too often is what melts a congested Wi-Fi: every repeat adds traffic, the
    // resends arrive later still, and it snowballs. Ask fast once, then rarely, then give up (keyframe).
    const long NackRetryUs = 20_000;     // wait this long before asking for the same frame again
    const int MaxNacksPerFrame = 4;
    const long StaleAfterUs = 3_000;     // a frame with no new shard for this long has lost some
    const long GiveUpAfterUs = 80_000;   // then ask for a keyframe instead
    const long KeyframeRetryUs = 250_000;
    const long FirstKeyframeGraceUs = 300_000; // the host sends one on registration anyway
    const uint MaxRepairableGap = 12;          // frames (~200 ms at 60 fps)

    readonly SortedDictionary<uint, Pending> _pending = new();
    uint _next;
    bool _needKeyframe = true;
    uint _newestSeen;
    long _gapSinceUs = -1, _lastKeyframeRequestUs = -1;
    readonly Dictionary<uint, (int Count, long LastUs)> _wholeFrameNacks = new();

    public long Delivered, Dropped, NacksSent, KeyframeRequests, RepairedByFec;

    public void OnPacket(ReadOnlySpan<byte> p, long nowUs, List<Frame> output)
    {
        if (p.Length < Protocol.VideoHeaderSize || p[0] != Protocol.UdpVideo) return;
        bool key = (p[1] & Protocol.FlagKeyframe) != 0;
        int shard = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
        int dataShards = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
        ushort hostDelay = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
        int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(p[12..]);
        ulong captureUs = BinaryPrimitives.ReadUInt64LittleEndian(p[16..]);
        int parityShards = BinaryPrimitives.ReadUInt16LittleEndian(p[24..]);
        int fecPercent = p[26], fecBlock = p[27];
        var payload = p[Protocol.VideoHeaderSize..];

        if (!_needKeyframe && frameId < _next) return; // late duplicate
        if (dataShards == 0 || shard >= dataShards + parityShards || size <= 0 || size > dataShards * S) return;
        if (frameId > _newestSeen) _newestSeen = frameId;

        if (!_pending.TryGetValue(frameId, out var f))
        {
            f = new Pending
            {
                FrameId = frameId, Data = new byte[dataShards * S], Size = size, DataShards = dataShards,
                ParityShards = parityShards, Parity = new byte[parityShards][], Have = new bool[dataShards + parityShards],
                Fec = parityShards > 0 && fecPercent > 0 ? new FecLayout(dataShards, fecPercent, fecBlock) : null,
                Key = key, CaptureUs = captureUs, HostDelay = hostDelay,
            };
            _pending[frameId] = f;
        }
        f.LastPacketUs = nowUs;
        if (f.Complete || f.Have[shard]) return;

        f.Have[shard] = true;
        int block;
        if (shard < dataShards)
        {
            payload[..Math.Min(payload.Length, S)].CopyTo(f.Data.AsSpan(shard * S));
            f.HaveData++;
            block = f.Fec?.BlockOfData(shard) ?? -1;
        }
        else
        {
            f.Parity[shard - dataShards] = payload[..Math.Min(payload.Length, S)].ToArray();
            block = f.Fec?.ParityPosition(shard).Block ?? -1;
        }
        if (!f.Complete && block >= 0) TryRepair(f, block);
        if (f.Complete) Deliver(nowUs, output);
    }

    /// <summary>Rebuilds a block's missing data shards once it has as many shards as data.</summary>
    void TryRepair(Pending f, int block)
    {
        var fec = f.Fec!;
        int k = fec.DataInBlock(block), p = fec.ParityCount(block);
        var haveData = new bool[k];
        var haveParity = new bool[p];
        int received = 0;
        for (int n = 0; n < k; n++) if (haveData[n] = f.Have[fec.DataShard(block, n)]) received++;
        for (int j = 0; j < p; j++) if (haveParity[j] = f.Have[fec.ParityShard(block, j)]) received++;
        if (received < k || haveData.All(h => h)) return;

        var data = new Memory<byte>[k];
        for (int n = 0; n < k; n++) data[n] = f.Data.AsMemory(fec.DataShard(block, n) * S, S);
        var parity = new Memory<byte>[p];
        for (int j = 0; j < p; j++) parity[j] = f.Parity[fec.ParityShard(block, j) - f.DataShards] ?? new byte[S];
        if (!ReedSolomon.Reconstruct(data, haveData, parity, haveParity)) return;

        for (int n = 0; n < k; n++)
        {
            int shard = fec.DataShard(block, n);
            if (f.Have[shard]) continue;
            f.Have[shard] = true;
            f.HaveData++;
        }
        if (!f.Repaired) RepairedByFec++;
        f.Repaired = true;
    }

    void Deliver(long nowUs, List<Frame> output)
    {
        if (_needKeyframe)
        {
            // Resynchronise on the newest complete keyframe.
            var kf = _pending.Values.LastOrDefault(x => x.Complete && x.Key);
            if (kf is null) return;
            foreach (var id in _pending.Keys.Where(id => id < kf.FrameId).ToList())
            {
                _pending.Remove(id);
                Dropped++;
            }
            _next = kf.FrameId;
            _needKeyframe = false;
        }
        while (_pending.TryGetValue(_next, out var f) && f.Complete)
        {
            _pending.Remove(_next);
            output.Add(new Frame(f.FrameId, f.Data, f.Size, f.Key, f.CaptureUs, f.HostDelay, nowUs));
            Delivered++;
            _next++;
            _gapSinceUs = -1;
        }
    }

    /// <summary>Call every few ms: sends NACKs and keyframe requests as needed.</summary>
    public void Tick(long nowUs, Action<uint, IReadOnlyList<ushort>> sendNack, Action requestKeyframe, List<Frame> output)
    {
        if (_needKeyframe)
        {
            // A keyframe in flight may only be missing a shard or two: repair it rather than re-request.
            foreach (var f in _pending.Values)
                if (f.Key && !f.Complete && nowUs - f.LastPacketUs > StaleAfterUs) NackMissing(f, nowUs, sendNack);
            if (_lastKeyframeRequestUs < 0) _lastKeyframeRequestUs = nowUs - KeyframeRetryUs + FirstKeyframeGraceUs;
            if (nowUs - _lastKeyframeRequestUs > KeyframeRetryUs)
            {
                _lastKeyframeRequestUs = nowUs;
                KeyframeRequests++;
                requestKeyframe();
            }
            return;
        }

        bool gap = _newestSeen >= _next && !(_pending.TryGetValue(_next, out var head) && head.Complete);
        if (!gap)
        {
            _gapSinceUs = -1;
            return;
        }
        if (_gapSinceUs < 0) _gapSinceUs = nowUs;

        // Many frames behind (a long Wi-Fi hiccup): re-requesting them all would only add to the
        // congestion, and a keyframe resynchronises faster.
        if (nowUs - _gapSinceUs > GiveUpAfterUs || _newestSeen - _next > MaxRepairableGap)
        {
            Dropped += _pending.Count;
            _pending.Clear();
            _wholeFrameNacks.Clear();
            _needKeyframe = true;
            _gapSinceUs = -1;
            _lastKeyframeRequestUs = nowUs;
            KeyframeRequests++;
            requestKeyframe();
            return;
        }

        for (uint id = _next; id <= _newestSeen; id++)
        {
            if (_pending.TryGetValue(id, out var f))
            {
                bool stale = id < _newestSeen || nowUs - f.LastPacketUs > StaleAfterUs;
                if (!f.Complete && stale) NackMissing(f, nowUs, sendNack);
                continue;
            }
            // Nothing of this frame arrived: ask for all of it, with the same pacing as partial frames.
            _wholeFrameNacks.TryGetValue(id, out var asked);
            if (asked.Count >= MaxNacksPerFrame || (asked.Count > 0 && nowUs - asked.LastUs < NackRetryUs)) continue;
            _wholeFrameNacks[id] = (asked.Count + 1, nowUs);
            sendNack(id, []);
            NacksSent++;
        }
        if (_wholeFrameNacks.Count > 64)
            foreach (var old in _wholeFrameNacks.Keys.Where(k => k < _next).ToList()) _wholeFrameNacks.Remove(old);
        Deliver(nowUs, output);
    }

    /// <summary>Asks again for the data shards of blocks that FEC can't rebuild on its own.</summary>
    void NackMissing(Pending f, long nowUs, Action<uint, IReadOnlyList<ushort>> sendNack)
    {
        if (f.Nacks >= MaxNacksPerFrame || (f.Nacks > 0 && nowUs - f.LastNackUs < NackRetryUs)) return;
        f.Nacks++;
        f.LastNackUs = nowUs;
        var missing = new List<ushort>();
        for (int i = 0; i < f.DataShards; i++)
        {
            if (f.Have[i]) continue;
            if (f.Fec is { } fec)
            {
                int b = fec.BlockOfData(i), received = 0;
                for (int n = 0; n < fec.DataInBlock(b); n++) if (f.Have[fec.DataShard(b, n)]) received++;
                for (int j = 0; j < fec.ParityCount(b); j++) if (f.Have[fec.ParityShard(b, j)]) received++;
                if (received >= fec.DataInBlock(b)) continue; // repairable: will be rebuilt locally
            }
            missing.Add((ushort)i);
        }
        // Keep each NACK in one datagram.
        for (int start = 0; start < missing.Count; start += 600)
        {
            sendNack(f.FrameId, missing.GetRange(start, Math.Min(600, missing.Count - start)));
            NacksSent++;
        }
    }
}
