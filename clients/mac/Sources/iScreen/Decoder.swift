import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Hardware HEVC decoding with VideoToolbox. Input is Annex-B (start codes) as sent by the host.
final class HEVCDecoder {
    /// Called synchronously from `decode` with each decoded picture.
    var onFrame: ((CVPixelBuffer, ReassembledFrame) -> Void)?
    var onError: ((String) -> Void)?

    private var session: VTDecompressionSession?
    private var format: CMVideoFormatDescription?
    private var parameterSets: [[UInt8]] = []

    /// Returns false when the frame couldn't be decoded (caller should ask for a keyframe).
    @discardableResult
    func decode(_ frame: ReassembledFrame) -> Bool {
        var vps: [UInt8]?, sps: [UInt8]?, pps: [UInt8]?
        var units: [ArraySlice<UInt8>] = []
        // The buffer is padded to whole FEC shards: only the first `size` bytes are the access unit.
        for nal in Self.splitAnnexB(Array(frame.data.prefix(frame.size))) {
            guard let header = nal.first else { continue }
            switch (header >> 1) & 0x3F {
            case 32: vps = Array(nal)
            case 33: sps = Array(nal)
            case 34: pps = Array(nal)
            case 35: continue // access unit delimiter
            default: units.append(nal)
            }
        }
        if let vps, let sps, let pps, [vps, sps, pps] != parameterSets {
            guard makeSession(vps: vps, sps: sps, pps: pps) else { return false }
            parameterSets = [vps, sps, pps]
        }
        guard let session, let format else { return false } // waiting for the first keyframe
        guard !units.isEmpty else { return true }

        // Length-prefixed NAL units (4-byte big-endian), as CoreMedia expects.
        var sample = [UInt8]()
        sample.reserveCapacity(frame.size + units.count * 4)
        for u in units {
            let n = UInt32(u.count)
            sample.append(contentsOf: [UInt8(n >> 24), UInt8((n >> 16) & 0xFF), UInt8((n >> 8) & 0xFF), UInt8(n & 0xFF)])
            sample.append(contentsOf: u)
        }

        var block: CMBlockBuffer?
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: sample.count,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
            dataLength: sample.count, flags: 0, blockBufferOut: &block)
        guard status == noErr, let block else { return false }
        status = sample.withUnsafeBytes { bytes in
            CMBlockBufferReplaceDataBytes(with: bytes.baseAddress!, blockBuffer: block,
                                          offsetIntoDestination: 0, dataLength: bytes.count)
        }
        guard status == noErr else { return false }

        var sampleBuffer: CMSampleBuffer?
        var sampleSize = sample.count
        status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
            sampleSizeEntryCount: 1, sampleSizeArray: &sampleSize, sampleBufferOut: &sampleBuffer)
        guard status == noErr, let sampleBuffer else { return false }

        // No asynchronous flag: the output handler runs before this call returns.
        let decodeStatus = VTDecompressionSessionDecodeFrame(
            session, sampleBuffer: sampleBuffer, flags: [], infoFlagsOut: nil
        ) { [weak self] status, _, imageBuffer, _, _ in
            guard let self else { return }
            if status == noErr, let imageBuffer {
                self.onFrame?(imageBuffer, frame)
            } else {
                self.onError?("decode error \(status)")
            }
        }
        if decodeStatus == kVTInvalidSessionErr {
            invalidate() // e.g. after sleep: rebuilt on the next keyframe
        }
        return decodeStatus == noErr
    }

    private func makeSession(vps: [UInt8], sps: [UInt8], pps: [UInt8]) -> Bool {
        invalidate()
        var newFormat: CMFormatDescription?
        let status: OSStatus = vps.withUnsafeBufferPointer { v in
            sps.withUnsafeBufferPointer { s in
                pps.withUnsafeBufferPointer { p in
                    let pointers: [UnsafePointer<UInt8>] = [v.baseAddress!, s.baseAddress!, p.baseAddress!]
                    let sizes: [Int] = [v.count, s.count, p.count]
                    return CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                        allocator: kCFAllocatorDefault, parameterSetCount: 3,
                        parameterSetPointers: pointers, parameterSetSizes: sizes,
                        nalUnitHeaderLength: 4, extensions: nil, formatDescriptionOut: &newFormat)
                }
            }
        }
        guard status == noErr, let newFormat else {
            onError?("HEVC parameter sets rejected (\(status))")
            return false
        }

        // Ask for the stream's native range so VideoToolbox doesn't convert it.
        let fullRange = (CMFormatDescriptionGetExtension(
            newFormat, extensionKey: kCMFormatDescriptionExtension_FullRangeVideo) as? Bool) ?? true
        let pixelFormat = fullRange ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange
                                    : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        let attributes: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: pixelFormat,
            kCVPixelBufferMetalCompatibilityKey: true,
            kCVPixelBufferIOSurfacePropertiesKey: [CFString: Any]() as CFDictionary,
        ]
        let specification: [CFString: Any] = [
            kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder: true,
        ]
        var newSession: VTDecompressionSession?
        let created = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault, formatDescription: newFormat,
            decoderSpecification: specification as CFDictionary,
            imageBufferAttributes: attributes as CFDictionary,
            outputCallback: nil, decompressionSessionOut: &newSession)
        guard created == noErr, let newSession else {
            onError?("VTDecompressionSessionCreate failed (\(created))")
            return false
        }
        VTSessionSetProperty(newSession, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        session = newSession
        format = newFormat
        return true
    }

    func invalidate() {
        if let session { VTDecompressionSessionInvalidate(session) }
        session = nil
        format = nil
        parameterSets = []
    }

    /// Splits an Annex-B byte stream into NAL units (without start codes).
    static func splitAnnexB(_ d: [UInt8]) -> [ArraySlice<UInt8>] {
        var result: [ArraySlice<UInt8>] = []
        var start = -1
        var i = 0
        let n = d.count
        while i + 2 < n {
            if d[i] == 0 && d[i + 1] == 0 && d[i + 2] == 1 {
                if start >= 0 {
                    var end = i
                    while end > start && d[end - 1] == 0 { end -= 1 } // zero byte of a 4-byte start code
                    if end > start { result.append(d[start..<end]) }
                }
                i += 3
                start = i
            } else {
                i += 1
            }
        }
        if start >= 0 && start < n { result.append(d[start..<n]) }
        return result
    }

    deinit { invalidate() }
}
