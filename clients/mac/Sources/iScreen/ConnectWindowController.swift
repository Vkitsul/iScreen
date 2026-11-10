import AppKit

final class ConnectWindowController: NSWindowController, NSWindowDelegate {
    var onWindowClose: (() -> Void)?

    private let hostField = NSTextField()
    private let pinField = NSTextField()
    private let bitrateField = NSTextField()
    private let modePopup = NSPopUpButton()
    private let fpsPopup = NSPopUpButton()
    private let colorPopup = NSPopUpButton()
    private let commandCheckbox = NSButton(checkboxWithTitle: "⌘ come Ctrl (⌘C, ⌘V…), ⌃ sinistro = tasto Windows",
                                           target: nil, action: nil)
    private let resolutionCheckbox = NSButton(checkboxWithTitle: "Adatta la risoluzione del PC a questo Mac (riempie lo schermo)",
                                              target: nil, action: nil)
    private let audioCheckbox = NSButton(checkboxWithTitle: "Audio del PC", target: nil, action: nil)
    private let clipboardCheckbox = NSButton(checkboxWithTitle: "Appunti condivisi (copia su un lato, incolla sull'altro)",
                                             target: nil, action: nil)
    private let errorLabel = NSTextField(wrappingLabelWithString: "")
    private let onConnect: (ConnectSettings) -> Void

    init(error: String?, onConnect: @escaping (ConnectSettings) -> Void) {
        self.onConnect = onConnect
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 480, height: 320),
                              styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "iScreen — connetti al PC"
        window.isReleasedWhenClosed = false
        super.init(window: window)
        window.delegate = self
        buildForm(error: error)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func label(_ text: String) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.alignment = .right
        return l
    }

    private func buildForm(error: String?) {
        let s = ConnectSettings.load()
        hostField.stringValue = s.host
        hostField.placeholderString = "es. 192.168.1.57"
        pinField.stringValue = s.pin
        pinField.placeholderString = "il PIN mostrato da iscreen-host"
        modePopup.addItems(withTitles: ["4:4:4 — testo e colori nitidi", "4:2:0 — meno banda"])
        modePopup.selectItem(at: s.use444 ? 0 : 1)
        bitrateField.stringValue = "\(s.bitrateMbps)"
        fpsPopup.addItems(withTitles: ["30", "60", "120"])
        fpsPopup.selectItem(withTitle: "\(s.fps)")
        colorPopup.addItems(withTitles: ColorMode.allCases.map { $0.label })
        colorPopup.selectItem(at: s.colorMode)
        commandCheckbox.state = s.commandAsControl ? .on : .off
        resolutionCheckbox.state = s.matchResolution ? .on : .off
        audioCheckbox.state = s.audio ? .on : .off
        clipboardCheckbox.state = s.shareClipboard ? .on : .off
        resolutionCheckbox.toolTip = "Durante il collegamento il PC passa alla risoluzione con le proporzioni di questo schermo "
            + "(es. 1920×1200 invece di 2560×1440) e la ripristina quando ti scolleghi."
        errorLabel.textColor = .systemRed
        errorLabel.stringValue = error ?? ""
        errorLabel.isHidden = error == nil

        let grid = NSGridView(views: [
            [label("PC Windows"), hostField],
            [label("PIN"), pinField],
            [label("Modalità"), modePopup],
            [label("Bitrate (Mbps)"), bitrateField],
            [label("FPS"), fpsPopup],
            [label("Colori"), colorPopup],
            [NSGridCell.emptyContentView, commandCheckbox],
            [NSGridCell.emptyContentView, resolutionCheckbox],
            [NSGridCell.emptyContentView, audioCheckbox],
            [NSGridCell.emptyContentView, clipboardCheckbox],
        ])
        grid.rowSpacing = 8
        grid.columnSpacing = 10
        grid.column(at: 0).xPlacement = .trailing
        hostField.widthAnchor.constraint(greaterThanOrEqualToConstant: 280).isActive = true

        let connect = NSButton(title: "Connetti", target: self, action: #selector(connect(_:)))
        connect.keyEquivalent = "\r"
        connect.bezelStyle = .rounded

        let stack = NSStackView(views: [grid, errorLabel, connect])
        stack.orientation = .vertical
        stack.alignment = .trailing
        stack.spacing = 14
        stack.edgeInsets = NSEdgeInsets(top: 20, left: 20, bottom: 20, right: 20)
        errorLabel.preferredMaxLayoutWidth = 420
        window?.contentView = stack
        window?.setContentSize(stack.fittingSize)
        window?.initialFirstResponder = s.host.isEmpty ? hostField : pinField
    }

    func showError(_ message: String) {
        errorLabel.stringValue = message
        errorLabel.isHidden = false
    }

    @objc private func connect(_ sender: Any?) {
        var s = ConnectSettings.load()
        s.host = hostField.stringValue.trimmingCharacters(in: .whitespaces)
        s.pin = pinField.stringValue.trimmingCharacters(in: .whitespaces)
        guard !s.host.isEmpty, !s.pin.isEmpty else {
            showError("Inserisci l'indirizzo del PC e il PIN.")
            return
        }
        s.use444 = modePopup.indexOfSelectedItem == 0
        s.bitrateMbps = max(5, Int(bitrateField.stringValue) ?? 80)
        s.fps = Int(fpsPopup.titleOfSelectedItem ?? "60") ?? 60
        s.colorMode = colorPopup.indexOfSelectedItem
        s.commandAsControl = commandCheckbox.state == .on
        s.matchResolution = resolutionCheckbox.state == .on
        s.audio = audioCheckbox.state == .on
        s.shareClipboard = clipboardCheckbox.state == .on
        (s.displayWidth, s.displayHeight) = Self.fullScreenPixels(of: window?.screen ?? NSScreen.main)
        s.save()
        onConnect(s)
    }

    /// The area a full-screen window gets, in pixels: below the notch on MacBooks that have one.
    private static func fullScreenPixels(of screen: NSScreen?) -> (Int, Int) {
        guard let screen else { return (0, 0) }
        let scale = screen.backingScaleFactor
        let height = screen.frame.height - screen.safeAreaInsets.top
        return (Int(screen.frame.width * scale), Int(height * scale))
    }

    func windowWillClose(_ notification: Notification) {
        onWindowClose?()
    }
}
