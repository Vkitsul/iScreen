import CoreVideo
import Foundation
import Network

struct ConnectSettings: Codable {
    var host = ""
    var pin = ""
    var use444 = true
    var bitrateMbps = 80
    var fps = 60
    var colorMode = ColorMode.hostMonitor.rawValue
    var commandAsControl = true
    /// Ask the PC to switch to the resolution that best fits this Mac's screen.
    var matchResolution = true
    /// Play the PC's audio on the Mac.
    var audio = true
    /// Copy on one side, paste on the other (text).
    var shareClipboard = true
    /// This Mac's full-screen area in pixels (filled in when connecting).
    var displayWidth = 0
    var displayHeight = 0

    private static let key = "connectSettings"

    init() {}

    // Missing keys fall back to defaults, so settings saved by older versions still load.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = ConnectSettings()
        host = try c.decodeIfPresent(String.self, forKey: .host) ?? d.host
        pin = try c.decodeIfPresent(String.self, forKey: .pin) ?? d.pin
        use444 = try c.decodeIfPresent(Bool.self, forKey: .use444) ?? d.use444
        bitrateMbps = try c.decodeIfPresent(Int.self, forKey: .bitrateMbps) ?? d.bitrateMbps
        fps = try c.decodeIfPresent(Int.self, forKey: .fps) ?? d.fps
        colorMode = try c.decodeIfPresent(Int.self, forKey: .colorMode) ?? d.colorMode
        commandAsControl = try c.decodeIfPresent(Bool.self, forKey: .commandAsControl) ?? d.commandAsControl
        matchResolution = try c.decodeIfPresent(Bool.self, forKey: .matchResolution) ?? d.matchResolution
        audio = try c.decodeIfPresent(Bool.self, forKey: .audio) ?? d.audio
        shareClipboard = try c.decodeIfPresent(Bool.self, forKey: .shareClipboard) ?? d.shareClipboard
        displayWidth = try c.decodeIfPresent(Int.self, forKey: .displayWidth) ?? d.displayWidth
        displayHeight = try c.decodeIfPresent(Int.self, forKey: .displayHeight) ?? d.displayHeight
    }

    static func load() -> ConnectSettings {
        guard let data = UserDefaults.standard.data(forKey: key),
              let s = try? JSONDecoder().decode(ConnectSettings.self, from: data) else { return ConnectSettings() }
        return s
    }

    func save() {
        if let data = try? JSONEncoder().encode(self) { UserDefaults.standard.set(data, forKey: Self.key) }
    }
}

struct StreamStats {
    var fps: Double = 0
    var latencyMs: Double = 0   // captured on the PC -> on the Mac screen
    var hostMs: Double = 0      // captured -> sent (mostly encoding)
    var mbps: Double = 0
    var rttMs: Double = 0
    var dropped = 0
    var nacks = 0
}

/// One streaming session: TCP control, UDP video, decode and render.
final class StreamClient {
    var onSessionInfo: ((SessionInfo) -> Void)?
    var onCursorShape: ((_ width: Int, _ height: Int, _ hotX: Int, _ hotY: Int, _ rgba: [UInt8]) -> Void)?
    var onCursorVisible: ((Bool) -> Void)?
    var onStats: ((StreamStats) -> Void)?
    var onClosed: ((String?) -> Void)?
    /// Text copied on the PC (main thread).
    var onClipboard: ((String) -> Void)?

    let settings: ConnectSettings
    private let renderer: Renderer
    private let decoder = HEVCDecoder()
    private let controlQueue = DispatchQueue(label: "iscreen.control")
    private let decodeQueue = DispatchQueue(label: "iscreen.decode", qos: .userInteractive)
    private var connection: NWConnection?
    private var pingTimer: DispatchSourceTimer?

    // Shared between threads, guarded by `lock`.
    private let lock = NSLock()
    private var running = true
    private var closed = false
    private var info: SessionInfo?
    private var resetReassembler = false
    private var pendingDecodes = 0
    private var clockOffsetUs: Int64 = 0 // host clock minus local clock
    private var rttUs: Int64 = 0
    private var lastKeyframeRequestUs: Int64 = 0
    private var statFrames = 0
    private var statLatencyUs: Int64 = 0
    private var statHostUs: Int64 = 0
    private var statBytes = 0
    private var statDropped = 0
    private var statNacks = 0

    // Decode queue only.
    private var renderThisFrame = true

    // Main thread only.
    private var pressedKeys = Set<UInt16>()

    init(settings: ConnectSettings, renderer: Renderer) {
        self.settings = settings
        self.renderer = renderer
        decoder.onFrame = { [weak self] pixelBuffer, frame in self?.present(pixelBuffer, frame) }
        decoder.onError = { [weak self] _ in self?.requestKeyframe() }
        renderer.onPresented = { [weak self] frame, presentUs in self?.recordPresented(frame, presentUs: presentUs) }
    }

    // MARK: - Lifecycle

    func start() {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.enableKeepalive = true
        tcp.connectionTimeout = 5
        let conn = NWConnection(host: NWEndpoint.Host(settings.host), port: NWEndpoint.Port(rawValue: Proto.controlPort)!,
                                using: NWParameters(tls: nil, tcp: tcp))
        connection = conn
        conn.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready: self?.receiveMessage()
            case .waiting(let error): self?.close("PC non raggiungibile (\(error.localizedDescription)). È avviato `iscreen-host serve`? Il firewall di Windows lo consente?")
            case .failed(let error): self?.close("Connessione fallita: \(error.localizedDescription)")
            default: break
            }
        }
        conn.start(queue: controlQueue)
    }

    func stop() {
        lock.lock()
        running = false
        lock.unlock()
        pingTimer?.cancel()
        connection?.cancel()
        decodeQueue.async { self.decoder.invalidate() }
    }

    private var isRunning: Bool {
        lock.lock()
        defer { lock.unlock() }
        return running
    }

    private func close(_ reason: String?) {
        lock.lock()
        let first = !closed
        closed = true
        lock.unlock()
        guard first else { return }
        stop()
        DispatchQueue.main.async { self.onClosed?(reason) }
    }

    // MARK: - Control channel

    private func send(_ message: MessageWriter) {
        connection?.send(content: message.finish(), completion: .contentProcessed { _ in })
    }

    private func receiveMessage() {
        guard let conn = connection else { return }
        conn.receive(minimumIncompleteLength: 4, maximumLength: 4) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            guard let data, data.count == 4, error == nil else {
                self.close(isComplete ? "Il PC ha chiuso la connessione" : "Connessione interrotta")
                return
            }
            let h = [UInt8](data)
            let length = Int(UInt32(h[0]) | UInt32(h[1]) << 8 | UInt32(h[2]) << 16 | UInt32(h[3]) << 24)
            guard length >= 1 && length <= 16 << 20 else {
                self.close("Messaggio non valido dal PC")
                return
            }
            conn.receive(minimumIncompleteLength: length, maximumLength: length) { body, _, _, error in
                guard let body, body.count == length, error == nil else {
                    self.close("Connessione interrotta")
                    return
                }
                let bytes = [UInt8](body)
                self.handle(type: bytes[0], payload: Array(bytes[1...]))
                if self.isRunning { self.receiveMessage() }
            }
        }
    }

    private func handle(type: UInt8, payload: [UInt8]) {
        guard let msg = Msg(rawValue: type) else { return }
        switch msg {
        case .challenge:
            var r = ByteReader(payload)
            guard r.u16() == Proto.version else {
                close("Versione del PC diversa: aggiorna host e client")
                return
            }
            let nonce = r.raw(16)
            send(MessageWriter(.hello)
                .u16(Proto.version).u16(0).u8(settings.use444 ? 1 : 0)
                .u32(UInt32(max(0, settings.bitrateMbps) * 1000)).u16(UInt16(clamping: settings.fps))
                .raw(Proto.pinMac(pin: settings.pin, nonce: nonce))
                // Our screen, so the PC can switch to the resolution that fills it.
                .u16(UInt16(clamping: settings.displayWidth)).u16(UInt16(clamping: settings.displayHeight))
                .u8((settings.matchResolution ? Proto.helloMatchResolution : 0) | (settings.audio ? Proto.helloAudio : 0)
                    | (settings.shareClipboard ? Proto.helloClipboard : 0)))

        case .reject:
            close("PIN errato: controlla il PIN mostrato da iscreen-host sul PC")

        case .sessionInfo:
            guard let newInfo = SessionInfo(payload: payload) else {
                close("Risposta del PC non valida")
                return
            }
            lock.lock()
            let first = info == nil
            info = newInfo
            if !first { resetReassembler = true }
            lock.unlock()
            if first {
                startVideo(newInfo)
            } else {
                // Monitor or resolution changed on the PC: start over from a fresh keyframe.
                decodeQueue.async { self.decoder.invalidate() }
                requestKeyframe()
            }
            DispatchQueue.main.async { self.onSessionInfo?(newInfo) }

        case .pong:
            var r = ByteReader(payload)
            let sent = Int64(r.u64()), host = Int64(r.u64()), now = Clock.nowUs()
            lock.lock()
            rttUs = now - sent
            clockOffsetUs = host - (sent + now) / 2
            lock.unlock()

        case .cursorShape:
            var r = ByteReader(payload)
            let w = Int(r.u16()), h = Int(r.u16()), hotX = Int(r.u16()), hotY = Int(r.u16())
            let rgba = r.raw(w * h * 4)
            guard w > 0, h > 0, rgba.count == w * h * 4 else { return }
            DispatchQueue.main.async { self.onCursorShape?(w, h, hotX, hotY, rgba) }

        case .cursorState:
            var r = ByteReader(payload)
            let visible = r.u8() != 0
            DispatchQueue.main.async { self.onCursorVisible?(visible) }

        case .clipboard:
            var r = ByteReader(payload)
            guard settings.shareClipboard, r.u8() == 1 else { return }
            let length = Int(r.u32())
            guard length <= Proto.maxClipboardBytes, let text = String(bytes: r.raw(length), encoding: .utf8) else { return }
            DispatchQueue.main.async { self.onClipboard?(text) }

        default:
            break
        }
    }

    private func requestKeyframe() {
        let now = Clock.nowUs()
        lock.lock()
        let allowed = now - lastKeyframeRequestUs > 200_000
        if allowed { lastKeyframeRequestUs = now }
        lock.unlock()
        if allowed { send(MessageWriter(.keyframeRequest)) }
    }

    private func everySecond() {
        send(MessageWriter(.ping).u64(UInt64(Clock.nowUs())))
        lock.lock()
        var s = StreamStats()
        s.fps = Double(statFrames)
        s.latencyMs = statFrames > 0 ? Double(statLatencyUs) / Double(statFrames) / 1000 : 0
        s.hostMs = statFrames > 0 ? Double(statHostUs) / Double(statFrames) / 1000 : 0
        s.mbps = Double(statBytes) * 8 / 1_000_000
        s.rttMs = Double(rttUs) / 1000
        s.dropped = statDropped
        s.nacks = statNacks
        statFrames = 0
        statLatencyUs = 0
        statHostUs = 0
        statBytes = 0
        lock.unlock()
        DispatchQueue.main.async { self.onStats?(s) }
    }

    // MARK: - Video

    private func startVideo(_ info: SessionInfo) {
        guard let address = Self.resolveIPv4(settings.host) else {
            close("Indirizzo del PC non valido")
            return
        }
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else {
            close("Impossibile creare il socket UDP")
            return
        }
        var bufferSize: Int32 = 4 << 20 // absorbs keyframe bursts
        setsockopt(fd, SOL_SOCKET, SO_RCVBUF, &bufferSize, socklen_t(MemoryLayout<Int32>.size))
        var timeout = timeval(tv_sec: 0, tv_usec: 2000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))

        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = info.udpPort.bigEndian
        addr.sin_addr = address
        let connected = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard connected == 0 else {
            Darwin.close(fd)
            close("Impossibile aprire il canale video UDP")
            return
        }

        let sessionId = info.sessionId
        let thread = Thread { [weak self] in self?.receiveLoop(fd: fd, sessionId: sessionId) }
        thread.name = "iscreen.udp"
        thread.qualityOfService = .userInteractive
        thread.start()

        let timer = DispatchSource.makeTimerSource(queue: controlQueue)
        timer.schedule(deadline: .now(), repeating: 1.0)
        timer.setEventHandler { [weak self] in self?.everySecond() }
        timer.resume()
        pingTimer = timer
    }

    private func receiveLoop(fd: Int32, sessionId: UInt32) {
        var reassembler = Reassembler()
        var buffer = [UInt8](repeating: 0, count: 2048)
        var frames: [ReassembledFrame] = []
        let register: [UInt8] = [Proto.udpRegister] + Self.le32(sessionId)
        var lastRegister: Int64 = 0
        var lastStats: Int64 = 0
        let audio = settings.audio ? AudioPlayer() : nil
        defer { audio?.stop() }

        while isRunning {
            lock.lock()
            if resetReassembler {
                resetReassembler = false
                reassembler = Reassembler()
            }
            lock.unlock()

            let before = Clock.nowUs()
            if before - lastRegister > 500_000 {
                register.withUnsafeBytes { _ = Darwin.send(fd, $0.baseAddress, $0.count, 0) }
                lastRegister = before
            }

            let n = buffer.withUnsafeMutableBytes { recv(fd, $0.baseAddress, $0.count, 0) }
            let now = Clock.nowUs()
            if n > 0 {
                buffer.withUnsafeBytes {
                    let packet = UnsafeRawBufferPointer(rebasing: $0[0..<n])
                    if packet[0] == Proto.udpAudio {
                        audio?.push(packet)
                    } else {
                        reassembler.onPacket(packet, nowUs: now, output: &frames)
                    }
                }
            }
            reassembler.tick(
                nowUs: now,
                sendNack: { frameId, frags in Self.sendNack(fd: fd, sessionId: sessionId, frameId: frameId, frags: frags) },
                requestKeyframe: { self.requestKeyframe() },
                output: &frames)

            if !frames.isEmpty {
                enqueueDecode(frames)
                frames.removeAll(keepingCapacity: true)
            }
            lock.lock()
            if n > 0 { statBytes += n }
            if now - lastStats > 250_000 {
                statDropped = reassembler.dropped
                statNacks = reassembler.nacksSent
                lastStats = now
            }
            lock.unlock()
        }
        Darwin.close(fd)
    }

    private func enqueueDecode(_ frames: [ReassembledFrame]) {
        lock.lock()
        pendingDecodes += frames.count
        lock.unlock()
        for frame in frames {
            decodeQueue.async { [weak self] in
                guard let self else { return }
                self.lock.lock()
                self.pendingDecodes -= 1
                // Behind? Decode everything (P-frames depend on it) but only show the newest.
                self.renderThisFrame = self.pendingDecodes == 0
                self.lock.unlock()
                if !self.decoder.decode(frame) { self.requestKeyframe() }
            }
        }
    }

    /// Decode queue, called from inside `decoder.decode`.
    private func present(_ pixelBuffer: CVPixelBuffer, _ frame: ReassembledFrame) {
        lock.lock()
        let info = self.info
        lock.unlock()
        guard renderThisFrame, let info else { return }
        renderer.render(pixelBuffer, frame: frame, info: info)
    }

    private func recordPresented(_ frame: ReassembledFrame, presentUs: Int64) {
        lock.lock()
        statFrames += 1
        statLatencyUs += presentUs + clockOffsetUs - Int64(frame.captureUs)
        statHostUs += Int64(frame.hostDelay100us) * 100
        lock.unlock()
    }

    func redraw() {
        decodeQueue.async { self.renderer.redraw() }
    }

    // MARK: - Input (main thread)

    func sendMouseMove(x: Float, y: Float) {
        send(MessageWriter(.mouseMove).f32(x).f32(y))
    }

    func sendMouseButton(_ button: UInt8, down: Bool) {
        send(MessageWriter(.mouseButton).u8(button).u8(down ? 1 : 0))
    }

    /// Text copied on the Mac.
    func sendClipboard(_ text: String) {
        let bytes = Array(text.utf8)
        guard settings.shareClipboard, bytes.count <= Proto.maxClipboardBytes else { return }
        send(MessageWriter(.clipboard).u8(1).u32(UInt32(bytes.count)).raw(bytes))
    }

    func sendWheel(dx: Float, dy: Float) {
        send(MessageWriter(.mouseWheel).f32(dx).f32(dy))
    }

    func sendKey(_ scancode: UInt16, down: Bool) {
        if down { pressedKeys.insert(scancode) } else { pressedKeys.remove(scancode) }
        send(MessageWriter(.key).u16(scancode).u8(down ? 1 : 0))
    }

    /// Focus lost: release everything so nothing stays stuck on the PC.
    func releaseAllKeys() {
        for k in pressedKeys { send(MessageWriter(.key).u16(k).u8(0)) }
        pressedKeys.removeAll()
    }

    // MARK: - Helpers

    private static func le32(_ v: UInt32) -> [UInt8] { (0..<4).map { UInt8(truncatingIfNeeded: v >> (8 * UInt32($0))) } }
    private static func le16(_ v: UInt16) -> [UInt8] { [UInt8(v & 0xFF), UInt8(v >> 8)] }

    private static func sendNack(fd: Int32, sessionId: UInt32, frameId: UInt32, frags: [UInt16]) {
        var p: [UInt8] = [Proto.udpNack]
        p += le32(sessionId)
        p += le32(frameId)
        p += le16(UInt16(frags.count))
        for f in frags { p += le16(f) }
        p.withUnsafeBytes { _ = Darwin.send(fd, $0.baseAddress, $0.count, 0) }
    }

    private static func resolveIPv4(_ host: String) -> in_addr? {
        var addr = in_addr()
        if inet_pton(AF_INET, host, &addr) == 1 { return addr }
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_DGRAM
        var result: UnsafeMutablePointer<addrinfo>?
        guard getaddrinfo(host, nil, &hints, &result) == 0, let first = result, let sa = first.pointee.ai_addr else {
            return nil
        }
        defer { freeaddrinfo(result) }
        return sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr }
    }
}
