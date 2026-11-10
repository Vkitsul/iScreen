import CryptoKit
import Foundation

/// Wire protocol v1 — keep in sync with docs/ARCHITETTURA.md and the Windows host.
enum Proto {
    static let version: UInt16 = 2 // 2: FEC fields in the video header
    static let controlPort: UInt16 = 52700
    static let videoHeaderSize = 28
    static let maxPayload = 1372 // shard size
    static let udpVideo: UInt8 = 1
    static let udpAudio: UInt8 = 2
    static let audioHeaderSize = 20
    static let helloMatchResolution: UInt8 = 1
    static let helloAudio: UInt8 = 2
    static let helloClipboard: UInt8 = 4
    static let maxClipboardBytes = 8 * 1024 * 1024
    static let udpRegister: UInt8 = 0x80
    static let udpNack: UInt8 = 0x81
    static let flagKeyframe: UInt8 = 1

    static func pinMac(pin: String, nonce: [UInt8]) -> [UInt8] {
        let key = SymmetricKey(data: Data(pin.utf8))
        return Array(HMAC<SHA256>.authenticationCode(for: nonce, using: key))
    }
}

enum Msg: UInt8 {
    case hello = 1
    case sessionInfo = 2
    case challenge = 3
    case reject = 4
    case mouseMove = 10
    case mouseButton = 11
    case mouseWheel = 12
    case key = 13
    case keyframeRequest = 20
    case ping = 30
    case pong = 31
    case cursorShape = 60
    case cursorState = 61
    case clipboard = 70
}

/// Builds one control message: u32 length, u8 type, payload (little-endian).
final class MessageWriter {
    private(set) var bytes: [UInt8] = [0, 0, 0, 0]

    init(_ type: Msg) { bytes.append(type.rawValue) }

    @discardableResult func u8(_ v: UInt8) -> MessageWriter { bytes.append(v); return self }
    @discardableResult func u16(_ v: UInt16) -> MessageWriter { append(v.littleEndian); return self }
    @discardableResult func u32(_ v: UInt32) -> MessageWriter { append(v.littleEndian); return self }
    @discardableResult func u64(_ v: UInt64) -> MessageWriter { append(v.littleEndian); return self }
    @discardableResult func f32(_ v: Float) -> MessageWriter { append(v.bitPattern.littleEndian); return self }
    @discardableResult func raw(_ v: [UInt8]) -> MessageWriter { bytes.append(contentsOf: v); return self }

    private func append<T: FixedWidthInteger>(_ v: T) {
        withUnsafeBytes(of: v) { bytes.append(contentsOf: $0) }
    }

    func finish() -> Data {
        var out = bytes
        let length = UInt32(out.count - 4)
        for i in 0..<4 { out[i] = UInt8(truncatingIfNeeded: length >> (8 * UInt32(i))) }
        return Data(out)
    }
}

/// Bounds-checked little-endian reader; reads past the end return 0 and set `overflow`.
struct ByteReader {
    private let bytes: [UInt8]
    private var pos = 0
    private(set) var overflow = false

    init(_ bytes: [UInt8]) { self.bytes = bytes }

    var remaining: Int { bytes.count - pos }

    private mutating func take(_ n: Int) -> ArraySlice<UInt8>? {
        guard pos + n <= bytes.count else { overflow = true; return nil }
        defer { pos += n }
        return bytes[pos..<(pos + n)]
    }

    private mutating func int<T: FixedWidthInteger>(_: T.Type) -> T {
        guard let s = take(MemoryLayout<T>.size) else { return 0 }
        var v: T = 0
        for (i, b) in s.enumerated() { v |= T(b) << (8 * i) }
        return v
    }

    mutating func u8() -> UInt8 { int(UInt8.self) }
    mutating func u16() -> UInt16 { int(UInt16.self) }
    mutating func u32() -> UInt32 { int(UInt32.self) }
    mutating func u64() -> UInt64 { int(UInt64.self) }
    mutating func f32() -> Float { Float(bitPattern: u32()) }
    mutating func raw(_ n: Int) -> [UInt8] {
        guard let s = take(n) else { return [] }
        return Array(s)
    }
}

struct SessionInfo: Equatable {
    var sessionId: UInt32
    var width: Int
    var height: Int
    var encWidth: Int
    var encHeight: Int
    var auxOffset: Int
    var mode: UInt8          // 0 = 4:2:0, 1 = 4:4:4 packed
    var fps: Int
    var primaries: [Float]   // rx ry gx gy bx by wx wy
    var gamma: Float
    var udpPort: UInt16

    var is444: Bool { mode == 1 }

    init?(payload: [UInt8]) {
        var r = ByteReader(payload)
        guard r.u16() == Proto.version else { return nil }
        sessionId = r.u32()
        width = Int(r.u16())
        height = Int(r.u16())
        encWidth = Int(r.u16())
        encHeight = Int(r.u16())
        auxOffset = Int(r.u16())
        mode = r.u8()
        _ = r.u8() // codec (1 = HEVC)
        _ = r.u8() // bit depth
        fps = Int(r.u16())
        primaries = (0..<8).map { _ in r.f32() }
        gamma = r.f32()
        udpPort = r.u16()
        if r.overflow || width == 0 || height == 0 { return nil }
    }
}

enum Clock {
    /// Monotonic microseconds (mach_absolute_time base, same as CACurrentMediaTime).
    static func nowUs() -> Int64 { Int64(DispatchTime.now().uptimeNanoseconds / 1000) }
}
