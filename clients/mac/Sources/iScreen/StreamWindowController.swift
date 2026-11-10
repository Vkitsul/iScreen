import AppKit

final class StreamWindowController: NSWindowController, NSWindowDelegate {
    private let streamView = StreamView(frame: NSRect(x: 0, y: 0, width: 1280, height: 720))
    private let settings: ConnectSettings
    private let onClose: (String?) -> Void
    private var client: StreamClient?
    private var renderer: Renderer?
    private var keyMonitor: Any?
    private var info: SessionInfo?
    private var sizedOnce = false
    private var closeReason: String?
    private var clipboardTimer: Timer?
    private var pasteboardChangeCount = NSPasteboard.general.changeCount

    init(settings: ConnectSettings, onClose: @escaping (String?) -> Void) {
        self.settings = settings
        self.onClose = onClose
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 720),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable],
                              backing: .buffered, defer: false)
        window.title = "iScreen — connessione a \(settings.host)…"
        window.backgroundColor = .black
        window.collectionBehavior = [.fullScreenPrimary]
        window.isReleasedWhenClosed = false
        window.contentView = streamView
        window.center()
        super.init(window: window)
        window.delegate = self
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func start() throws {
        let renderer = try Renderer(layer: streamView.metalLayer)
        let client = StreamClient(settings: settings, renderer: renderer)
        self.renderer = renderer
        self.client = client
        streamView.client = client
        streamView.onResize = { [weak client] in client?.redraw() }

        client.onSessionInfo = { [weak self] info in self?.apply(info) }
        client.onCursorShape = { [weak self] w, h, hotX, hotY, rgba in
            self?.streamView.setCursorShape(width: w, height: h, hotX: hotX, hotY: hotY, rgba: rgba)
        }
        client.onCursorVisible = { [weak self] visible in self?.streamView.setCursorVisible(visible) }
        client.onStats = { [weak self] stats in self?.show(stats) }
        client.onClosed = { [weak self] reason in
            self?.closeReason = reason
            self?.window?.close()
        }
        client.onClipboard = { [weak self] text in self?.pasteFromPC(text) }

        showWindow(nil)
        window?.makeFirstResponder(streamView)
        installKeyMonitor()
        if settings.shareClipboard { watchPasteboard() }
        client.start()
    }

    // MARK: - Shared clipboard (text)

    /// macOS has no "pasteboard changed" notification: poll its change counter.
    private func watchPasteboard() {
        pasteboardChangeCount = NSPasteboard.general.changeCount
        clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.3, repeats: true) { [weak self] _ in
            self?.checkPasteboard()
        }
    }

    private func checkPasteboard() {
        let pasteboard = NSPasteboard.general
        guard pasteboard.changeCount != pasteboardChangeCount else { return }
        pasteboardChangeCount = pasteboard.changeCount
        if let text = pasteboard.string(forType: .string), !text.isEmpty {
            client?.sendClipboard(text) // copied on the Mac: ready to paste on the PC
        }
    }

    private func pasteFromPC(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        pasteboardChangeCount = pasteboard.changeCount // our own change: don't send it back
    }

    private func apply(_ info: SessionInfo) {
        let sizeChanged = self.info.map { $0.width != info.width || $0.height != info.height } ?? true
        self.info = info
        streamView.videoSize = CGSize(width: info.width, height: info.height)
        streamView.metalLayer.colorspace = ColorSpaces.make(ColorMode(rawValue: settings.colorMode) ?? .hostMonitor, info: info)
        window?.contentAspectRatio = NSSize(width: info.width, height: info.height)
        if (!sizedOnce || sizeChanged), window?.styleMask.contains(.fullScreen) == false {
            sizedOnce = true
            actualSize(nil)
        }
        client?.redraw()
    }

    /// One PC pixel per Mac pixel (shrunk to fit the screen if needed).
    @objc func actualSize(_ sender: Any?) {
        guard let info, let window, let screen = window.screen ?? NSScreen.main else { return }
        let scale = screen.backingScaleFactor
        var size = NSSize(width: CGFloat(info.width) / scale, height: CGFloat(info.height) / scale)
        let titleBar = window.frame.height - window.contentLayoutRect.height
        let maxW = screen.visibleFrame.width, maxH = screen.visibleFrame.height - titleBar
        if size.width > maxW || size.height > maxH {
            let f = min(maxW / size.width, maxH / size.height)
            size = NSSize(width: floor(size.width * f), height: floor(size.height * f))
        }
        window.setContentSize(size)
        window.center()
    }

    private func show(_ s: StreamStats) {
        guard let info else { return }
        var title = "iScreen — \(info.width)×\(info.height) \(info.is444 ? "4:4:4" : "4:2:0")"
        title += String(format: " · %.0f fps · %.0f ms (PC %.0f) · %.0f Mbps", s.fps, s.latencyMs, s.hostMs, s.mbps)
        if s.dropped > 0 { title += " · frame persi \(s.dropped)" }
        window?.title = title
    }

    // MARK: - Keyboard

    private func installKeyMonitor() {
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp, .flagsChanged]) { [weak self] event in
            guard let self, let window = self.window, event.window === window,
                  window.firstResponder === self.streamView else { return event }
            return self.forward(event) ? nil : event
        }
    }

    /// Sends the event to the PC; returns true if it must not reach the Mac's menus.
    private func forward(_ event: NSEvent) -> Bool {
        guard let client else { return false }
        switch event.type {
        case .keyDown, .keyUp:
            let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
            // Mac-side shortcuts: ⌘Q and every ⌃⌘ combination (full screen, actual size…).
            let isQuit = flags == .command && event.charactersIgnoringModifiers?.lowercased() == "q"
            if flags.contains([.control, .command]) || isQuit { return false }
            if let sc = KeyMap.scancode(forKeyCode: event.keyCode) {
                client.sendKey(sc, down: event.type == .keyDown)
            }
            return true
        case .flagsChanged:
            if event.keyCode == KeyMap.capsLockKeyCode {
                client.sendKey(KeyMap.capsLockScancode, down: true)
                client.sendKey(KeyMap.capsLockScancode, down: false)
            } else if let m = KeyMap.modifier(forKeyCode: event.keyCode, commandAsControl: settings.commandAsControl) {
                client.sendKey(m.scancode, down: (event.modifierFlags.rawValue & m.deviceMask) != 0)
            }
            return false // AppKit keeps tracking modifier state
        default:
            return false
        }
    }

    // MARK: - NSWindowDelegate

    func windowDidResignKey(_ notification: Notification) {
        client?.releaseAllKeys()
    }

    func windowWillClose(_ notification: Notification) {
        clipboardTimer?.invalidate()
        clipboardTimer = nil
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        client?.releaseAllKeys()
        client?.stop()
        client = nil
        onClose(closeReason)
    }
}
