import AVFoundation
import os

/// Plays the PC's audio: 16-bit stereo PCM packets (5 ms each) go into a ring buffer that an
/// AVAudioSourceNode drains. ~30 ms of buffering absorbs Wi-Fi jitter; if more piles up (the two
/// sound cards' clocks drift apart, or a burst arrives after a stall) we skip ahead to stay in sync.
final class AudioPlayer {
    private let engine = AVAudioEngine()
    private var node: AVAudioSourceNode?
    private var sampleRate: Double = 0

    // Ring buffer of interleaved L/R floats, shared with the real-time render thread under `lock`.
    private let capacity = 192_000 // frames: 1 s at the highest rate we accept
    private let ring: UnsafeMutablePointer<Float>
    private var readPos = 0
    private var writePos = 0
    private var available = 0
    private var primed = false
    private var targetFrames = 1_440
    private var maxFrames = 5_760
    private let lock = OSAllocatedUnfairLock()

    init() {
        ring = .allocate(capacity: capacity * 2)
        ring.initialize(repeating: 0, count: capacity * 2)
    }

    deinit {
        stop()
        ring.deallocate()
    }

    /// One audio packet from the UDP thread (header layout in docs/ARCHITETTURA.md).
    func push(_ p: UnsafeRawBufferPointer) {
        guard p.count >= Proto.audioHeaderSize else { return }
        let frames = Int(UInt16(littleEndian: p.loadUnaligned(fromByteOffset: 2, as: UInt16.self)))
        let rate = Double(UInt32(littleEndian: p.loadUnaligned(fromByteOffset: 8, as: UInt32.self)))
        guard frames > 0, p.count >= Proto.audioHeaderSize + frames * 4, rate >= 8_000, rate <= 192_000 else { return }
        if rate != sampleRate { start(rate: rate) }

        lock.withLockUnchecked {
            for i in 0..<frames {
                if available == capacity { // full: drop the oldest frame
                    readPos = (readPos + 1) % capacity
                    available -= 1
                }
                let o = Proto.audioHeaderSize + i * 4
                ring[writePos * 2] = Float(Int16(littleEndian: p.loadUnaligned(fromByteOffset: o, as: Int16.self))) / 32768
                ring[writePos * 2 + 1] = Float(Int16(littleEndian: p.loadUnaligned(fromByteOffset: o + 2, as: Int16.self))) / 32768
                writePos = (writePos + 1) % capacity
                available += 1
            }
            if available > maxFrames { // too far behind: jump back to the target delay
                let skip = available - targetFrames
                readPos = (readPos + skip) % capacity
                available -= skip
            }
        }
    }

    /// Real-time thread: fill `frameCount` frames of the deinterleaved stereo output.
    private func render(frameCount: Int, buffers: UnsafeMutableAudioBufferListPointer) {
        guard buffers.count >= 2,
              let left = buffers[0].mData?.assumingMemoryBound(to: Float.self),
              let right = buffers[1].mData?.assumingMemoryBound(to: Float.self) else { return }
        lock.withLockUnchecked {
            var n = 0
            if !primed && available >= targetFrames { primed = true }
            if primed {
                n = min(frameCount, available)
                for i in 0..<n {
                    left[i] = ring[readPos * 2]
                    right[i] = ring[readPos * 2 + 1]
                    readPos = (readPos + 1) % capacity
                }
                available -= n
                if n < frameCount { primed = false } // ran dry: rebuild the cushion before playing on
            }
            for i in n..<frameCount {
                left[i] = 0
                right[i] = 0
            }
        }
    }

    private func start(rate: Double) {
        stop()
        sampleRate = rate
        lock.withLockUnchecked {
            readPos = 0
            writePos = 0
            available = 0
            primed = false
            targetFrames = Int(rate * 0.030)
            maxFrames = Int(rate * 0.120)
        }
        guard let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: 2) else { return }
        let node = AVAudioSourceNode(format: format) { [weak self] _, _, frameCount, bufferList -> OSStatus in
            self?.render(frameCount: Int(frameCount), buffers: UnsafeMutableAudioBufferListPointer(bufferList))
            return noErr
        }
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: format) // the mixer resamples to the Mac's rate
        do {
            try engine.start()
            self.node = node
        } catch {
            engine.detach(node)
            NSLog("iScreen audio: \(error.localizedDescription)")
        }
    }

    func stop() {
        engine.stop()
        if let node {
            engine.detach(node)
            self.node = nil
        }
        sampleRate = 0
    }
}
