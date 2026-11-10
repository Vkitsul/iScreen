import Foundation

struct ReassembledFrame {
    let frameId: UInt32
    let data: [UInt8] // padded to whole shards; the access unit is data[0..<size]
    let size: Int
    let key: Bool
    let captureUs: UInt64
    let hostDelay100us: UInt16
    let completedUs: Int64
}

/// UDP shards -> access units, delivered strictly in order. Lost data shards are first rebuilt from
/// Reed-Solomon parity (no round trip); only what FEC can't recover is NACKed, and a gap that can't
/// be repaired quickly triggers a keyframe request. Port of FrameReassembler.cs.
final class Reassembler {
    private static let shardSize = Proto.maxPayload

    private final class Pending {
        let frameId: UInt32
        var data: [UInt8]   // dataShards * shardSize, zero padded
        var parity: [UInt8] // parityShards * shardSize
        let size: Int
        let dataShards: Int
        let parityShards: Int
        var have: [Bool]    // dataShards + parityShards
        var haveData = 0
        let fec: FecLayout?
        let key: Bool
        var repaired = false
        let captureUs: UInt64
        let hostDelay: UInt16
        var lastPacketUs: Int64 = 0
        var lastNackUs: Int64 = 0
        var nacks = 0
        var complete: Bool { haveData == dataShards }

        init(frameId: UInt32, size: Int, dataShards: Int, parityShards: Int, fec: FecLayout?,
             key: Bool, captureUs: UInt64, hostDelay: UInt16) {
            self.frameId = frameId
            data = [UInt8](repeating: 0, count: dataShards * Reassembler.shardSize)
            parity = [UInt8](repeating: 0, count: parityShards * Reassembler.shardSize)
            self.size = size
            self.dataShards = dataShards
            self.parityShards = parityShards
            have = [Bool](repeating: false, count: dataShards + parityShards)
            self.fec = fec
            self.key = key
            self.captureUs = captureUs
            self.hostDelay = hostDelay
        }

        /// Shards of a block that arrived (data + parity).
        func received(inBlock b: Int) -> Int {
            guard let fec else { return 0 }
            var n = 0
            for i in 0..<fec.dataInBlock(b) where have[fec.dataShard(b, i)] { n += 1 }
            for j in 0..<fec.parityCount(b) where have[fec.parityShard(b, j)] { n += 1 }
            return n
        }
    }

    // NACK pacing. Re-asking too often is what melts a congested Wi-Fi: every repeat adds traffic, the
    // resends arrive later still, and it snowballs. Ask fast once, then rarely, then give up (keyframe).
    private let nackRetryUs: Int64 = 20_000
    private let maxNacksPerFrame = 4
    private let staleAfterUs: Int64 = 3_000
    private let giveUpAfterUs: Int64 = 80_000
    private let keyframeRetryUs: Int64 = 250_000
    private let firstKeyframeGraceUs: Int64 = 300_000
    private let maxRepairableGap: UInt32 = 12 // frames (~200 ms at 60 fps)

    private var pending: [UInt32: Pending] = [:]
    private var next: UInt32 = 0
    private var needKeyframe = true
    private var newestSeen: UInt32 = 0
    private var gapSinceUs: Int64 = -1
    private var lastKeyframeRequestUs: Int64 = -1
    private var wholeFrameNacks: [UInt32: (count: Int, lastUs: Int64)] = [:]

    private(set) var delivered = 0
    private(set) var dropped = 0
    private(set) var nacksSent = 0
    private(set) var keyframeRequests = 0
    private(set) var repairedByFec = 0

    func onPacket(_ p: UnsafeRawBufferPointer, nowUs: Int64, output: inout [ReassembledFrame]) {
        guard p.count >= Proto.videoHeaderSize, p[0] == Proto.udpVideo else { return }
        let key = (p[1] & Proto.flagKeyframe) != 0
        let shard = Int(UInt16(littleEndian: p.loadUnaligned(fromByteOffset: 2, as: UInt16.self)))
        let dataShards = Int(UInt16(littleEndian: p.loadUnaligned(fromByteOffset: 4, as: UInt16.self)))
        let hostDelay = UInt16(littleEndian: p.loadUnaligned(fromByteOffset: 6, as: UInt16.self))
        let frameId = UInt32(littleEndian: p.loadUnaligned(fromByteOffset: 8, as: UInt32.self))
        let size = Int(UInt32(littleEndian: p.loadUnaligned(fromByteOffset: 12, as: UInt32.self)))
        let captureUs = UInt64(littleEndian: p.loadUnaligned(fromByteOffset: 16, as: UInt64.self))
        let parityShards = Int(UInt16(littleEndian: p.loadUnaligned(fromByteOffset: 24, as: UInt16.self)))
        let fecPercent = Int(p[26]), fecBlock = Int(p[27])
        let S = Reassembler.shardSize

        if !needKeyframe && frameId < next { return } // late duplicate
        guard dataShards > 0, shard < dataShards + parityShards, size > 0, size <= dataShards * S else { return }
        if frameId > newestSeen { newestSeen = frameId }

        let f: Pending
        if let existing = pending[frameId] {
            f = existing
        } else {
            let fec = parityShards > 0 && fecPercent > 0 && fecBlock > 0
                ? FecLayout(dataShards: dataShards, percent: fecPercent, maxBlockData: fecBlock) : nil
            f = Pending(frameId: frameId, size: size, dataShards: dataShards, parityShards: parityShards,
                        fec: fec, key: key, captureUs: captureUs, hostDelay: hostDelay)
            pending[frameId] = f
        }
        f.lastPacketUs = nowUs
        guard !f.complete, !f.have[shard] else { return }

        f.have[shard] = true
        let n = min(p.count - Proto.videoHeaderSize, S)
        let src = p.baseAddress!.advanced(by: Proto.videoHeaderSize)
        var block = -1
        if shard < dataShards {
            f.data.withUnsafeMutableBytes { $0.baseAddress!.advanced(by: shard * S).copyMemory(from: src, byteCount: n) }
            f.haveData += 1
            if let fec = f.fec { block = fec.blockOfData(shard) }
        } else {
            f.parity.withUnsafeMutableBytes {
                $0.baseAddress!.advanced(by: (shard - dataShards) * S).copyMemory(from: src, byteCount: n)
            }
            if let fec = f.fec { block = fec.parityBlock(shard) }
        }
        if !f.complete && block >= 0 { tryRepair(f, block: block) }
        if f.complete { deliver(nowUs: nowUs, output: &output) }
    }

    /// Rebuilds a block's missing data shards once it has as many shards as data.
    private func tryRepair(_ f: Pending, block b: Int) {
        guard let fec = f.fec else { return }
        let k = fec.dataInBlock(b), p = fec.parityCount(b), S = Reassembler.shardSize
        let haveData = (0..<k).map { f.have[fec.dataShard(b, $0)] }
        let haveParity = (0..<p).map { f.have[fec.parityShard(b, $0)] }
        guard haveData.contains(false), f.received(inBlock: b) >= k else { return }

        let ok: Bool = f.data.withUnsafeMutableBufferPointer { dataBuffer in
            f.parity.withUnsafeBufferPointer { parityBuffer in
                let data = (0..<k).map { dataBuffer.baseAddress! + fec.dataShard(b, $0) * S }
                let parity = (0..<p).map { UnsafePointer(parityBuffer.baseAddress! + (fec.parityShard(b, $0) - f.dataShards) * S) }
                return ReedSolomon.reconstruct(data: data, haveData: haveData, parity: parity, haveParity: haveParity, size: S)
            }
        }
        guard ok else { return }
        for n in 0..<k where !f.have[fec.dataShard(b, n)] {
            f.have[fec.dataShard(b, n)] = true
            f.haveData += 1
        }
        if !f.repaired { repairedByFec += 1 }
        f.repaired = true
    }

    private func deliver(nowUs: Int64, output: inout [ReassembledFrame]) {
        if needKeyframe {
            // Resynchronise on the newest complete keyframe.
            guard let kf = pending.values.filter({ $0.complete && $0.key }).max(by: { $0.frameId < $1.frameId }) else { return }
            for id in pending.keys where id < kf.frameId {
                pending.removeValue(forKey: id)
                dropped += 1
            }
            next = kf.frameId
            needKeyframe = false
        }
        while let f = pending[next], f.complete {
            pending.removeValue(forKey: next)
            output.append(ReassembledFrame(frameId: f.frameId, data: f.data, size: f.size, key: f.key,
                                           captureUs: f.captureUs, hostDelay100us: f.hostDelay, completedUs: nowUs))
            delivered += 1
            next &+= 1
            gapSinceUs = -1
        }
    }

    /// Call every few ms: sends NACKs and keyframe requests as needed.
    func tick(nowUs: Int64, sendNack: (UInt32, [UInt16]) -> Void, requestKeyframe: () -> Void,
              output: inout [ReassembledFrame]) {
        if needKeyframe {
            // A keyframe in flight may only be missing a shard or two: repair it rather than re-request.
            for f in pending.values where f.key && !f.complete && nowUs - f.lastPacketUs > staleAfterUs {
                nackMissing(f, nowUs: nowUs, sendNack: sendNack)
            }
            if lastKeyframeRequestUs < 0 { lastKeyframeRequestUs = nowUs - keyframeRetryUs + firstKeyframeGraceUs }
            if nowUs - lastKeyframeRequestUs > keyframeRetryUs {
                lastKeyframeRequestUs = nowUs
                keyframeRequests += 1
                requestKeyframe()
            }
            return
        }

        let headComplete = pending[next]?.complete ?? false
        guard newestSeen >= next && !headComplete else {
            gapSinceUs = -1
            return
        }
        if gapSinceUs < 0 { gapSinceUs = nowUs }

        // Many frames behind (a long Wi-Fi hiccup): re-requesting them all would only add to the
        // congestion, and a keyframe resynchronises faster.
        if nowUs - gapSinceUs > giveUpAfterUs || newestSeen &- next > maxRepairableGap {
            dropped += pending.count
            pending.removeAll()
            wholeFrameNacks.removeAll()
            needKeyframe = true
            gapSinceUs = -1
            lastKeyframeRequestUs = nowUs
            keyframeRequests += 1
            requestKeyframe()
            return
        }

        var id = next
        var scanned = 0
        while id <= newestSeen && scanned < 256 {
            if let f = pending[id] {
                let stale = id < newestSeen || nowUs - f.lastPacketUs > staleAfterUs
                if !f.complete && stale { nackMissing(f, nowUs: nowUs, sendNack: sendNack) }
            } else {
                // Nothing of this frame arrived: ask for all of it, paced like partial frames.
                let asked = wholeFrameNacks[id] ?? (count: 0, lastUs: 0)
                if asked.count < maxNacksPerFrame && (asked.count == 0 || nowUs - asked.lastUs >= nackRetryUs) {
                    wholeFrameNacks[id] = (count: asked.count + 1, lastUs: nowUs)
                    sendNack(id, [])
                    nacksSent += 1
                }
            }
            id &+= 1
            scanned += 1
        }
        if wholeFrameNacks.count > 64 {
            for key in wholeFrameNacks.keys where key < next { wholeFrameNacks.removeValue(forKey: key) }
        }
        deliver(nowUs: nowUs, output: &output)
    }

    /// Asks again for the data shards of blocks that FEC can't rebuild on its own.
    private func nackMissing(_ f: Pending, nowUs: Int64, sendNack: (UInt32, [UInt16]) -> Void) {
        guard f.nacks < maxNacksPerFrame, f.nacks == 0 || nowUs - f.lastNackUs >= nackRetryUs else { return }
        f.nacks += 1
        f.lastNackUs = nowUs
        var missing: [UInt16] = []
        for i in 0..<f.dataShards where !f.have[i] {
            if let fec = f.fec {
                let b = fec.blockOfData(i)
                if f.received(inBlock: b) >= fec.dataInBlock(b) { continue } // rebuilt locally
            }
            missing.append(UInt16(i))
        }
        var start = 0
        while start < missing.count {
            let end = min(start + 600, missing.count) // keep each NACK in one datagram
            sendNack(f.frameId, Array(missing[start..<end]))
            nacksSent += 1
            start = end
        }
    }
}
