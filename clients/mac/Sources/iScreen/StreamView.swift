import AppKit
import QuartzCore

/// Hosts the CAMetalLayer and turns mouse events into protocol messages. Keyboard events are
/// handled by StreamWindowController (it needs to see ⌘-shortcuts before the menu does).
final class StreamView: NSView {
    let metalLayer = CAMetalLayer()
    weak var client: StreamClient?
    var onResize: (() -> Void)?

    /// Source size in pixels (from SessionInfo).
    var videoSize = CGSize(width: 1920, height: 1080) {
        didSet { window?.invalidateCursorRects(for: self) }
    }

    private var cursor: NSCursor = .arrow
    private var cursorShape: (w: Int, h: Int, hotX: Int, hotY: Int, rgba: [UInt8])?
    private var cursorVisible = true
    private let hiddenCursor = NSCursor(image: NSImage(size: NSSize(width: 1, height: 1), flipped: false) { _ in true },
                                        hotSpot: .zero)
    private var trackingArea: NSTrackingArea?
    private var scrollRemainder = CGPoint.zero

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layerContentsRedrawPolicy = .duringViewResize
        metalLayer.backgroundColor = NSColor.black.cgColor
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func makeBackingLayer() -> CALayer { metalLayer }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var isOpaque: Bool { true }

    override func layout() {
        super.layout()
        updateDrawableSize()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        updateDrawableSize()
        rebuildCursor()
    }

    private func updateDrawableSize() {
        let scale = window?.backingScaleFactor ?? 2
        metalLayer.contentsScale = scale
        let size = CGSize(width: max(1, bounds.width * scale), height: max(1, bounds.height * scale))
        if metalLayer.drawableSize != size {
            metalLayer.drawableSize = size
            onResize?()
        }
        window?.invalidateCursorRects(for: self)
    }

    /// Where the video sits inside the view, in points (same fit as the renderer).
    var videoRect: CGRect {
        let scale = window?.backingScaleFactor ?? 2
        let px = Renderer.fitRect(content: videoSize, in: CGSize(width: bounds.width * scale, height: bounds.height * scale))
        // Metal's viewport origin is top-left; NSView's is bottom-left.
        return CGRect(x: px.minX / scale, y: bounds.height - px.maxY / scale, width: px.width / scale, height: px.height / scale)
    }

    // MARK: - Cursor (drawn locally by macOS: zero latency)

    func setCursorShape(width: Int, height: Int, hotX: Int, hotY: Int, rgba: [UInt8]) {
        cursorShape = (width, height, hotX, hotY, rgba)
        rebuildCursor()
    }

    func setCursorVisible(_ visible: Bool) {
        cursorVisible = visible
        window?.invalidateCursorRects(for: self)
        (visible ? cursor : hiddenCursor).set()
    }

    private func rebuildCursor() {
        guard let s = cursorShape, s.w > 0, s.h > 0,
              let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: s.w, pixelsHigh: s.h,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bitmapFormat: [.alphaNonpremultiplied],
                                         bytesPerRow: s.w * 4, bitsPerPixel: 32),
              let dst = rep.bitmapData else { return }
        s.rgba.withUnsafeBytes { src in _ = memcpy(dst, src.baseAddress!, s.rgba.count) }
        // One host pixel covers (videoRect / videoSize) points on the Mac.
        let pointsPerPixel = videoSize.width > 0 ? videoRect.width / videoSize.width : 0.5
        let image = NSImage(size: NSSize(width: CGFloat(s.w) * pointsPerPixel, height: CGFloat(s.h) * pointsPerPixel))
        image.addRepresentation(rep)
        cursor = NSCursor(image: image, hotSpot: NSPoint(x: CGFloat(s.hotX) * pointsPerPixel, y: CGFloat(s.hotY) * pointsPerPixel))
        window?.invalidateCursorRects(for: self)
        if let window, window.isKeyWindow, cursorVisible,
           videoRect.contains(convert(window.mouseLocationOutsideOfEventStream, from: nil)) {
            cursor.set()
        }
    }

    override func resetCursorRects() {
        addCursorRect(videoRect, cursor: cursorVisible ? cursor : hiddenCursor)
    }

    override func cursorUpdate(with event: NSEvent) {
        (cursorVisible ? cursor : hiddenCursor).set()
    }

    // MARK: - Mouse

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let trackingArea { removeTrackingArea(trackingArea) }
        let area = NSTrackingArea(rect: .zero,
                                  options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect, .cursorUpdate],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        trackingArea = area
    }

    private func sendPosition(_ event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        let r = videoRect
        guard r.width > 0, r.height > 0 else { return }
        let x = Float(min(max((p.x - r.minX) / r.width, 0), 1))
        let y = Float(min(max(1 - (p.y - r.minY) / r.height, 0), 1))
        client?.sendMouseMove(x: x, y: y)
    }

    override func mouseMoved(with event: NSEvent) { sendPosition(event) }
    override func mouseDragged(with event: NSEvent) { sendPosition(event) }
    override func rightMouseDragged(with event: NSEvent) { sendPosition(event) }
    override func otherMouseDragged(with event: NSEvent) { sendPosition(event) }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        sendPosition(event)
        client?.sendMouseButton(0, down: true)
    }

    override func mouseUp(with event: NSEvent) { client?.sendMouseButton(0, down: false) }

    override func rightMouseDown(with event: NSEvent) {
        sendPosition(event)
        client?.sendMouseButton(1, down: true)
    }

    override func rightMouseUp(with event: NSEvent) { client?.sendMouseButton(1, down: false) }

    override func otherMouseDown(with event: NSEvent) {
        sendPosition(event)
        if let b = Self.button(event) { client?.sendMouseButton(b, down: true) }
    }

    override func otherMouseUp(with event: NSEvent) {
        if let b = Self.button(event) { client?.sendMouseButton(b, down: false) }
    }

    private static func button(_ event: NSEvent) -> UInt8? {
        switch event.buttonNumber {
        case 2: return 2 // middle
        case 3: return 3 // back
        case 4: return 4 // forward
        default: return nil
        }
    }

    override func scrollWheel(with event: NSEvent) {
        // Windows: +1 notch (120 units) scrolls up. macOS deltas already include "natural scrolling".
        var dx = event.scrollingDeltaX, dy = event.scrollingDeltaY
        if event.hasPreciseScrollingDeltas {
            // Trackpad / Magic Mouse: points. ~40 pt per notch feels close to a wheel on Windows.
            dx /= 40
            dy /= 40
        }
        scrollRemainder.x += dx
        scrollRemainder.y += dy
        // Send fractional notches: the host accumulates them, and modern Windows apps scroll smoothly.
        if abs(scrollRemainder.x) >= 0.05 || abs(scrollRemainder.y) >= 0.05 {
            client?.sendWheel(dx: Float(-scrollRemainder.x), dy: Float(scrollRemainder.y))
            scrollRemainder = .zero
        }
    }
}
