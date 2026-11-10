import AppKit

@main
enum IScreenApp {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.regular)
        withExtendedLifetime(delegate) { app.run() }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var connectWindow: ConnectWindowController?
    private var streamWindow: StreamWindowController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = makeMenu()
        showConnect(error: nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func showConnect(error: String?) {
        let controller = ConnectWindowController(error: error) { [weak self] settings in self?.connect(settings) }
        controller.onWindowClose = { [weak self] in
            // Closing the form without a stream running quits the app.
            if self?.streamWindow == nil { NSApp.terminate(nil) }
        }
        connectWindow = controller
        controller.showWindow(nil)
        controller.window?.center()
    }

    private func connect(_ settings: ConnectSettings) {
        let stream = StreamWindowController(settings: settings) { [weak self] reason in
            self?.streamWindow = nil
            self?.showConnect(error: reason)
        }
        do {
            try stream.start()
        } catch {
            connectWindow?.showError(error.localizedDescription)
            return
        }
        streamWindow = stream
        connectWindow?.close()
        connectWindow = nil
    }

    private func makeMenu() -> NSMenu {
        let main = NSMenu()

        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Informazioni su iScreen",
                        action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Nascondi iScreen", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: "Esci da iScreen", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let edit = NSMenu(title: "Modifica")
        edit.addItem(withTitle: "Taglia", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copia", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Incolla", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Seleziona tutto", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        main.addItem(editItem)

        let viewItem = NSMenuItem()
        let view = NSMenu(title: "Vista")
        let fullScreen = NSMenuItem(title: "Schermo intero", action: #selector(NSWindow.toggleFullScreen(_:)), keyEquivalent: "f")
        fullScreen.keyEquivalentModifierMask = [.control, .command]
        view.addItem(fullScreen)
        let actual = NSMenuItem(title: "Dimensione reale (1 pixel PC = 1 pixel Mac)",
                                action: #selector(StreamWindowController.actualSize(_:)), keyEquivalent: "1")
        actual.keyEquivalentModifierMask = [.control, .command]
        view.addItem(actual)
        viewItem.submenu = view
        main.addItem(viewItem)

        return main
    }
}
