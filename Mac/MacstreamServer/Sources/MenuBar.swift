import AppKit
import ServiceManagement

/// The Mac app's face: the menu-bar item, the Dock icon (on by default, "Show in Dock" turns
/// it off) and a small settings window. Clicking the Dock icon, or opening Macstream again from
/// Launchpad, shows the window; the Dock icon's menu has Settings… and Quit.
///
/// All three show the same things: connection status, what the PC sees, a switch for the PC's
/// mouse and keyboard, clipboard sync state, pairing (paired PCs, this Mac's key, forget them),
/// Start at Login, the log, and Quit. Quit releases anything the PC holds and stops the server;
/// the run-server.sh script (and its log window) then ends by itself.
///
/// Create it and call its methods on the main thread, except `setConnected`, which may be
/// called from any thread. Turn the menu bar (and with it the Dock icon) off with
/// MACSTREAM_MENU_BAR=0.
final class MenuBarController: NSObject, NSMenuDelegate, NSApplicationDelegate, NSWindowDelegate {
    private static let showInDockKey = "ShowInDock"

    private let statusItem: NSStatusItem
    private let statusLine = NSMenuItem(title: "Waiting for the PC", action: nil, keyEquivalent: "")
    private let screenLine = NSMenuItem(title: "Showing: this Mac’s screen", action: nil, keyEquivalent: "")
    private let inputItem = NSMenuItem(title: "Mouse and keyboard from the PC", action: nil, keyEquivalent: "")
    private let clipboardLine = NSMenuItem(title: "Clipboard sync: off (switch it in the Windows app)", action: nil, keyEquivalent: "")
    private let pairedLine = NSMenuItem(title: "Paired PCs: …", action: nil, keyEquivalent: "")
    private let keyLine = NSMenuItem(title: "This Mac’s key: …", action: nil, keyEquivalent: "")
    private let forgetItem = NSMenuItem(title: "Forget Paired PCs…", action: nil, keyEquivalent: "")
    private let loginItem = NSMenuItem(title: "Start at Login", action: nil, keyEquivalent: "")
    private let logPath: String?
    private let inputAvailable: Bool
    private let setInputPaused: (Bool) -> Void
    private let pairingInfo: () -> (names: [String], fingerprint: String)
    private let forgetPairedClients: () -> Void
    private let quit: () -> Void
    private var inputPaused = false
    private var connected = false
    private var showing: String?
    private var clipboardOn = false

    // Settings window (made the first time it is shown).
    private var window: NSWindow?
    private let wStatus = NSTextField(labelWithString: "")
    private let wShowing = NSTextField(labelWithString: "")
    private let wClipboard = NSTextField(labelWithString: "")
    private let wPaired = NSTextField(labelWithString: "")
    private let wKey = NSTextField(labelWithString: "")
    private var wInput: NSButton!
    private var wLogin: NSButton!
    private var wDock: NSButton!
    private var wForget: NSButton!

    init(inputAvailable: Bool, logPath: String?, setInputPaused: @escaping (Bool) -> Void,
         pairingInfo: @escaping () -> (names: [String], fingerprint: String),
         forgetPairedClients: @escaping () -> Void,
         quit: @escaping () -> Void) {
        self.logPath = logPath
        self.inputAvailable = inputAvailable
        self.setInputPaused = setInputPaused
        self.pairingInfo = pairingInfo
        self.forgetPairedClients = forgetPairedClients
        self.quit = quit
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()

        UserDefaults.standard.register(defaults: [Self.showInDockKey: true])
        NSApp.delegate = self
        NSApp.mainMenu = makeMainMenu()
        applyDockSetting()

        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.delegate = self

        let title = NSMenuItem(title: "Macstream", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        screenLine.isEnabled = false
        screenLine.isHidden = true   // shown once a PC has connected
        screenLine.toolTip = "Choose this in the Windows app: Settings > Mac screen"
        menu.addItem(screenLine)
        menu.addItem(NSMenuItem.separator())

        inputItem.target = self
        inputItem.action = #selector(toggleInput)
        if inputAvailable {
            inputItem.state = .on
            inputItem.toolTip = "Untick to stop the PC's mouse and keyboard; video keeps running"
        } else {
            inputItem.title = "Mouse and keyboard from the PC: turned off at start-up"
            inputItem.isEnabled = false
        }
        menu.addItem(inputItem)
        clipboardLine.isEnabled = false
        menu.addItem(clipboardLine)
        menu.addItem(NSMenuItem.separator())

        pairedLine.isEnabled = false
        menu.addItem(pairedLine)
        keyLine.isEnabled = false
        keyLine.toolTip = "The Windows app shows the same key in Settings for the Mac it is paired with"
        menu.addItem(keyLine)
        forgetItem.target = self
        forgetItem.action = #selector(confirmForget)
        forgetItem.toolTip = "Disconnects; each PC must confirm a 6-digit code again next time"
        menu.addItem(forgetItem)
        menu.addItem(NSMenuItem.separator())

        loginItem.target = self
        loginItem.action = #selector(toggleLoginItem)
        loginItem.toolTip = "Start Macstream (in this menu bar) when you log in to the Mac"
        menu.addItem(loginItem)
        let settingsItem = NSMenuItem(title: "Settings…", action: #selector(showSettingsAction), keyEquivalent: ",")
        settingsItem.target = self
        menu.addItem(settingsItem)
        let logItem = NSMenuItem(title: "Open Log", action: #selector(openLog), keyEquivalent: "")
        logItem.target = self
        logItem.isEnabled = logPath != nil
        menu.addItem(logItem)
        menu.addItem(NSMenuItem.separator())

        let quitItem = NSMenuItem(title: "Quit Macstream", action: #selector(quitServer), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)

        statusItem.menu = menu
        updateButton()
    }

    /// Shows whether a PC is connected. Safe to call from any thread.
    func setConnected(_ value: Bool) {
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.connected = value
            self.statusLine.title = value ? "PC connected" : "Waiting for the PC"
            self.updateButton()
            self.refreshWindow()
        }
    }

    /// Shows what the PC sees (this Mac's screen or an extra display); nil hides the line.
    /// Call on the main thread.
    func setShowing(_ text: String?) {
        showing = text
        screenLine.title = text ?? ""
        screenLine.isHidden = text == nil
        refreshWindow()
    }

    /// Shows whether clipboard sync is on. Call on the main thread.
    func setClipboardSync(_ on: Bool) {
        clipboardOn = on
        clipboardLine.title = clipboardText
        refreshWindow()
    }

    private var clipboardText: String {
        clipboardOn
            ? "Clipboard sync: on (switch it in the Windows app)"
            : "Clipboard sync: off (switch it in the Windows app)"
    }

    // Refresh the pairing and login lines each time the menu opens.
    func menuWillOpen(_ menu: NSMenu) {
        let info = pairingInfo()
        pairedLine.title = pairedText(info.names)
        keyLine.title = "This Mac’s key: \(info.fingerprint)"
        forgetItem.isEnabled = !info.names.isEmpty
        updateLoginItem()
    }

    private func pairedText(_ names: [String]) -> String {
        names.isEmpty ? "Paired PCs: none yet" : "Paired PCs: \(names.joined(separator: ", "))"
    }

    private func updateButton() {
        guard let button = statusItem.button else { return }
        if let image = NSImage(systemSymbolName: "display", accessibilityDescription: "Macstream") {
            image.isTemplate = true
            button.image = image
            button.title = ""
        } else {
            button.image = nil
            button.title = "MS"
        }
        // Dimmed while no PC is connected.
        button.appearsDisabled = !connected
        button.toolTip = "Macstream: \(statusLine.title)"
    }

    private func updateLoginItem() {
        switch SMAppService.mainApp.status {
        case .enabled:
            loginItem.state = .on
            loginItem.title = "Start at Login"
        case .requiresApproval:
            loginItem.state = .mixed
            loginItem.title = "Start at Login (allow it in System Settings)"
        default:
            loginItem.state = .off
            loginItem.title = "Start at Login"
        }
    }

    // MARK: - Dock icon and app menu

    private var showInDock: Bool { UserDefaults.standard.bool(forKey: Self.showInDockKey) }

    private func applyDockSetting() {
        NSApp.setActivationPolicy(showInDock ? .regular : .accessory)
    }

    /// The menus at the top of the screen while the settings window is in front.
    private func makeMainMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        main.addItem(appItem)
        let appMenu = NSMenu(title: "Macstream")
        appMenu.addItem(withTitle: "Settings…", action: #selector(showSettingsAction), keyEquivalent: ",").target = self
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: "Hide Macstream", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: "Quit Macstream", action: #selector(quitServer), keyEquivalent: "q").target = self
        appItem.submenu = appMenu

        let windowItem = NSMenuItem()
        main.addItem(windowItem)
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowMenu.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = windowMenu
        return main
    }

    // Clicking the Dock icon (or opening Macstream again from Launchpad) shows the settings.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSettings()
        return false
    }

    // The Dock icon's menu (macOS adds Options and Quit itself).
    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        let menu = NSMenu()
        let settings = NSMenuItem(title: "Settings…", action: #selector(showSettingsAction), keyEquivalent: "")
        settings.target = self
        menu.addItem(settings)
        return menu
    }

    // Quit from the Dock or Cmd+Q: the same clean shutdown as the menu bar's Quit.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        quit()          // releases held keys, stops the stream and exits
        return .terminateNow
    }

    // MARK: - Settings window

    @objc private func showSettingsAction() { showSettings() }

    func showSettings() {
        if window == nil { window = makeWindow() }
        refreshWindow()
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    private func makeWindow() -> NSWindow {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 440, height: 300),
                              styleMask: [.titled, .closable, .miniaturizable],
                              backing: .buffered, defer: false)
        window.title = "Macstream"
        window.isReleasedWhenClosed = false
        window.delegate = self

        func heading(_ text: String) -> NSTextField {
            let label = NSTextField(labelWithString: text)
            label.font = .boldSystemFont(ofSize: NSFont.systemFontSize)
            return label
        }
        func note(_ text: String) -> NSTextField {
            let label = NSTextField(wrappingLabelWithString: text)
            label.textColor = .secondaryLabelColor
            label.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
            label.preferredMaxLayoutWidth = 400
            return label
        }

        wInput = NSButton(checkboxWithTitle: "Mouse and keyboard from the PC", target: self, action: #selector(toggleInput))
        wInput.isEnabled = inputAvailable
        wLogin = NSButton(checkboxWithTitle: "Start at Login", target: self, action: #selector(toggleLoginItem))
        wDock = NSButton(checkboxWithTitle: "Show Macstream in the Dock", target: self, action: #selector(toggleDock))
        wForget = NSButton(title: "Forget Paired PCs…", target: self, action: #selector(confirmForget))
        let logButton = NSButton(title: "Open Log", target: self, action: #selector(openLog))
        logButton.isEnabled = logPath != nil
        let quitButton = NSButton(title: "Quit Macstream", target: self, action: #selector(quitServer))

        let buttons = NSStackView(views: [logButton, quitButton])
        buttons.orientation = .horizontal
        buttons.spacing = 8

        let stack = NSStackView(views: [
            heading("Connection"), wStatus, wShowing,
            note("Mirror or extra display, its size and the MacBook screen beside it are chosen in the Windows app: Settings > Mac screen."),
            heading("From the PC"), wInput, wClipboard,
            heading("Pairing"), wPaired, wKey, wForget,
            heading("This Mac"), wLogin, wDock,
            buttons,
        ])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.edgeInsets = NSEdgeInsets(top: 18, left: 20, bottom: 18, right: 20)
        stack.setCustomSpacing(10, after: wShowing)
        stack.setCustomSpacing(10, after: wForget)
        stack.setCustomSpacing(12, after: wClipboard)
        stack.setCustomSpacing(14, after: wDock)
        stack.translatesAutoresizingMaskIntoConstraints = false

        let content = NSView()
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            stack.widthAnchor.constraint(greaterThanOrEqualToConstant: 440),
        ])
        window.contentView = content
        window.setContentSize(content.fittingSize)
        window.center()
        return window
    }

    /// Brings the window up to date (only once it has been made).
    private func refreshWindow() {
        guard window != nil else { return }
        wStatus.stringValue = connected ? "PC connected" : "Waiting for the PC"
        wShowing.stringValue = showing ?? (connected ? "" : "Showing: nothing until a PC connects")
        wShowing.isHidden = wShowing.stringValue.isEmpty
        wClipboard.stringValue = clipboardText
        wInput.state = inputAvailable && !inputPaused ? .on : .off
        if !inputAvailable { wInput.title = "Mouse and keyboard from the PC: turned off at start-up" }
        let info = pairingInfo()
        wPaired.stringValue = pairedText(info.names)
        wKey.stringValue = "This Mac’s key: \(info.fingerprint)"
        wForget.isEnabled = !info.names.isEmpty
        switch SMAppService.mainApp.status {
        case .enabled: wLogin.state = .on; wLogin.title = "Start at Login"
        case .requiresApproval: wLogin.state = .mixed; wLogin.title = "Start at Login (allow it in System Settings)"
        default: wLogin.state = .off; wLogin.title = "Start at Login"
        }
        wDock.state = showInDock ? .on : .off
    }

    func windowDidBecomeKey(_ notification: Notification) {
        refreshWindow()   // e.g. pairing changed while it was in the background
    }

    // MARK: - Actions

    @objc private func toggleInput() {
        inputPaused.toggle()
        inputItem.state = inputPaused ? .off : .on
        setInputPaused(inputPaused)
        refreshWindow()
    }

    @objc private func toggleDock() {
        UserDefaults.standard.set(!showInDock, forKey: Self.showInDockKey)
        applyDockSetting()
        print("\(ServerText.mainTag) Dock icon \(showInDock ? "on" : "off") (settings)")
        // Leaving the Dock makes macOS push the window back; keep it in front.
        DispatchQueue.main.async { [weak self] in
            NSApp.activate(ignoringOtherApps: true)
            self?.window?.makeKeyAndOrderFront(nil)
            self?.refreshWindow()
        }
    }

    @objc private func confirmForget() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "Forget all paired PCs?"
        alert.informativeText = "A connected PC is disconnected. Next time, each PC shows a 6-digit code "
            + "and must be allowed on this Mac again."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Cancel")   // first button = default (Return)
        alert.addButton(withTitle: "Forget")
        if alert.runModal() == .alertSecondButtonReturn {
            forgetPairedClients()
        }
        refreshWindow()
    }

    @objc private func toggleLoginItem() {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled || service.status == .requiresApproval {
                try service.unregister()
                print("\(ServerText.mainTag) Start at Login turned off")
            } else {
                try service.register()
                print("\(ServerText.mainTag) Start at Login turned on: \(Bundle.main.bundlePath)")
            }
        } catch {
            print("\(ServerText.mainTag) Start at Login could not be changed: \(error.localizedDescription)")
            NSApp.activate(ignoringOtherApps: true)
            let alert = NSAlert()
            alert.messageText = "Start at Login could not be changed"
            alert.informativeText = "\(error.localizedDescription)\n\nYou can also add Macstream under "
                + "System Settings → General → Login Items."
            alert.runModal()
        }
        if service.status == .requiresApproval {
            SMAppService.openSystemSettingsLoginItems()
        }
        updateLoginItem()
        refreshWindow()
    }

    @objc private func openLog() {
        guard let logPath else { return }
        NSWorkspace.shared.open(URL(fileURLWithPath: logPath))
    }

    @objc private func quitServer() {
        quit()
    }
}
