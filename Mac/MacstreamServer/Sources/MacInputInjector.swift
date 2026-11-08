import Foundation
import CoreGraphics
import IOKit
import IOKit.hidsystem

/// Turns INPUT_EVENT packets from the Windows viewer into macOS mouse and keyboard events
/// (Windows -> Mac control). See Protocol/PROTOCOL.md for the wire format.
///
/// Safety rules:
/// - Every packet is length-checked; coordinates must be finite and are clamped to the
///   streamed display, so a bad packet can never move the pointer off that display.
/// - Only mouse buttons 0-2 and Mac key codes 0-127 are accepted, and the injector remembers
///   which buttons and keys it pressed. When the viewer disconnects, sends ReleaseAll, or stops
///   sending anything for `watchdogTimeout`, every one of them is released, so a pulled cable
///   never leaves a key or button stuck down.
/// - Key codes are Mac virtual key codes (the Windows side maps its keys). Modifier state is
///   tracked here and set explicitly on every event, so Cmd+C or Cmd-click work regardless of
///   what the system thinks is held.
/// - Posting events needs the macOS Accessibility permission for Macstream. Without it
///   macOS silently drops the events; we log that once instead of trying to work around it.
final class MacInputInjector {
    enum Kind: UInt8 {
        case mouseMove = 0x01
        case mouseDown = 0x02
        case mouseUp = 0x03
        case keyDown = 0x04
        case keyUp = 0x05
        case scroll = 0x06
        case releaseAll = 0x07
        case heartbeat = 0x08
        case ownScreenEnter = 0x09
        case relativeMove = 0x0A
    }

    /// Where the Mac's own screen is, seen from the PC (the PC edge the mouse left through).
    enum OwnScreenSide: UInt8 {
        case left = 0, right = 1, above = 2, below = 3
    }

    /// INPUT_FEEDBACK (0x0041) payload kinds, Mac -> PC.
    private enum Feedback: UInt8 {
        case entered = 0x01          // the PC's mouse is now on this Mac's own screen
        case unavailable = 0x02      // no own screen to go to (lid closed, ...)
        case backAcrossEdge = 0x03   // f32 along: came back over the edge facing the PC
        case backOnStreamed = 0x04   // f32 x, f32 y: the pointer is on the streamed display
    }

    /// The client sends a heartbeat every 250 ms while connected. If nothing at all arrives for
    /// this long, held buttons are released (the drag simply ends, like letting go of the mouse).
    private static let watchdogTimeout: TimeInterval = 1.0
    private static let maxScrollPixels: Int32 = 10_000
    private static let maxKeyCode: UInt16 = 0x7F

    // Mac virtual key codes (HIToolbox kVK_*) that are modifiers, and the flag each sets.
    private static let modifierFlags: [UInt16: CGEventFlags] = [
        0x37: .maskCommand, 0x36: .maskCommand,       // Command, RightCommand
        0x38: .maskShift, 0x3C: .maskShift,           // Shift, RightShift
        0x3A: .maskAlternate, 0x3D: .maskAlternate,   // Option, RightOption
        0x3B: .maskControl, 0x3E: .maskControl,       // Control, RightControl
        0x3F: .maskSecondaryFn,                       // Function
    ]
    private static let capsLockKey: UInt16 = 0x39
    private static let arrowKeys: ClosedRange<UInt16> = 0x7B...0x7E
    // Keypad keys (a real Mac keyboard marks these with the numeric-pad flag).
    private static let keypadKeys: Set<UInt16> = [0x41, 0x43, 0x45, 0x47, 0x4B, 0x4C, 0x4E, 0x51,
                                                  0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5C]
    // F-keys and the navigation block (a real Mac keyboard marks these with the Fn flag).
    private static let fnFlagKeys: Set<UInt16> = [0x7A, 0x78, 0x63, 0x76, 0x60, 0x61, 0x62, 0x64, 0x65,
                                                  0x6D, 0x67, 0x6F, 0x69, 0x6B, 0x71, 0x6A, 0x40, 0x4F,
                                                  0x50, 0x5A, 0x72, 0x73, 0x74, 0x75, 0x77, 0x79]

    private let queue = DispatchQueue(label: "com.macstream.input", qos: .userInteractive)
    private let source = CGEventSource(stateID: .hidSystemState)
    private var displayID: CGDirectDisplayID?
    private var heldButtons: Set<UInt8> = []
    private var heldKeys: Set<UInt16> = []
    private var capsLockOn = false
    /// True once Caps Lock was switched through macOS itself (see toggleCapsLock).
    private var capsLockViaSystem = false
    private var lastPoint: CGPoint?
    private var lastPacketAt = Date.distantPast
    private var watchdog: DispatchSourceTimer?
    private var warnedNoPermission = false
    private var hasPermission = false
    private var permissionCheckedAt = Date.distantPast
    private var loggedFirstEvent = false
    private var loggedFirstKey = false
    /// Set from the menu bar: while paused, everything from the PC is ignored.
    private var paused = false
    /// While the PC's mouse is on this Mac's own screen (beside the PC's monitor): which
    /// display and which side of the PC it is on. Mouse moves are relative then.
    private var ownScreen: (id: CGDirectDisplayID, side: OwnScreenSide)?
    private var loggedOwnScreen = false
    private var loggedNoOwnScreen = false
    private var lastStrayFeedbackAt = Date.distantPast

    /// Sends an INPUT_FEEDBACK payload to the viewer (set by Main.swift before any input
    /// arrives). Called on the injector's queue.
    var sendFeedback: ((Data) -> Void)?

    /// Whether Windows -> Mac input is allowed at all (see Main.swift for the policy).
    let enabled: Bool
    /// Whether keyboard events are accepted too (MACSTREAM_KEYBOARD=0 allows mouse only).
    let keyboardEnabled: Bool

    init(enabled: Bool, keyboardEnabled: Bool) {
        self.enabled = enabled
        self.keyboardEnabled = enabled && keyboardEnabled
    }

    /// Asks macOS for the Accessibility permission (needed to post input events). Shows the
    /// system prompt once if it has not been granted; returns the current state.
    @discardableResult
    static func requestPermissionIfNeeded() -> Bool {
        if CGPreflightPostEventAccess() { return true }
        print("\(ServerText.mainTag) Accessibility permission needed for mouse/keyboard control: allow Macstream in System Settings → Privacy & Security → Accessibility, then restart the server")
        return CGRequestPostEventAccess()
    }

    /// Sets the display the video shows; coordinates are mapped onto it. nil stops injection.
    func setTargetDisplay(_ id: CGDirectDisplayID?) {
        queue.async { [weak self] in
            guard let self else { return }
            if id == nil { self.releaseAllLocked(); self.releaseKeysLocked() }
            self.displayID = id
            self.lastPoint = nil
            self.ownScreen = nil
        }
    }

    /// Called from FrameServer's queue with one INPUT_EVENT payload.
    func handle(payload: Data) {
        guard enabled else { return }
        let bytes = [UInt8](payload)
        queue.async { [weak self] in
            self?.process(bytes)
        }
    }

    /// Pauses or resumes the PC's mouse and keyboard (menu bar). Pausing releases whatever the
    /// PC is holding, so nothing stays stuck.
    func setPaused(_ value: Bool) {
        queue.async { [weak self] in
            guard let self else { return }
            self.paused = value
            if value {
                self.releaseAllLocked()
                self.releaseKeysLocked()
            }
        }
    }

    /// Called when the viewer disconnects.
    func clientDisconnected() {
        queue.async { [weak self] in
            self?.releaseAllLocked()
            self?.releaseKeysLocked()
            self?.stopWatchdog()
            self?.ownScreen = nil
        }
    }

    /// Called on shutdown: releases held buttons before the process exits.
    func shutdown() {
        queue.sync {
            releaseAllLocked()
            releaseKeysLocked()
            stopWatchdog()
        }
    }

    // MARK: - Packet handling (on `queue`)

    private func process(_ bytes: [UInt8]) {
        guard !paused else { return }
        guard let first = bytes.first, let kind = Kind(rawValue: first) else { return }
        lastPacketAt = Date()
        startWatchdogIfNeeded()

        switch kind {
        case .heartbeat:
            return
        case .releaseAll:
            guard bytes.count == 1 else { return }
            releaseAllLocked()
            releaseKeysLocked()
        case .mouseMove:
            // u8 kind, f32 x, f32 y. A position on the picture also means the PC's mouse is
            // back on the streamed display.
            guard bytes.count == 9,
                  let point = displayPoint(x: readFloat(bytes, 1), y: readFloat(bytes, 5)) else { return }
            ownScreen = nil
            postMove(to: point)
        case .mouseDown, .mouseUp:
            // u8 kind, u8 button, u8 clickCount, f32 x, f32 y. On the own screen the click
            // happens where the pointer is (the PC's coordinates are for the picture).
            guard bytes.count == 11, bytes[1] <= 2 else { return }
            let point: CGPoint
            if let own = ownScreen {
                point = clamped(currentLocation(), to: CGDisplayBounds(own.id))
            } else if let p = displayPoint(x: readFloat(bytes, 3), y: readFloat(bytes, 7)) {
                point = p
            } else {
                return
            }
            let clicks = Int64(min(max(bytes[2], 1), 3))
            postButton(bytes[1], down: kind == .mouseDown, clickCount: clicks, at: point)
        case .ownScreenEnter:
            // u8 kind, u8 side, f32 along (0..1 along the PC's edge)
            guard bytes.count == 6, let side = OwnScreenSide(rawValue: bytes[1]) else { return }
            let along = readFloat(bytes, 2)
            guard along.isFinite else { return }
            enterOwnScreen(side: side, along: CGFloat(min(max(along, 0), 1)))
        case .relativeMove:
            // u8 kind, f32 dx, f32 dy in pixels of the streamed picture
            guard bytes.count == 9 else { return }
            let dx = readFloat(bytes, 1)
            let dy = readFloat(bytes, 5)
            guard dx.isFinite, dy.isFinite else { return }
            moveOnOwnScreen(dx: CGFloat(min(max(dx, -10_000), 10_000)),
                            dy: CGFloat(min(max(dy, -10_000), 10_000)))
        case .scroll:
            // u8 kind, i32 dx, i32 dy (pixels; +dy = scroll up / content moves down, +dx = right)
            guard bytes.count == 9 else { return }
            let dx = clampScroll(readInt32(bytes, 1))
            let dy = clampScroll(readInt32(bytes, 5))
            postScroll(dx: dx, dy: dy)
        case .keyDown, .keyUp:
            // u8 kind, u16 Mac virtual key code. Like the mouse, only while a display is being
            // streamed (the viewer is looking at the Mac).
            guard keyboardEnabled, bytes.count == 3, displayID != nil else { return }
            let key = UInt16(bytes[1]) | (UInt16(bytes[2]) << 8)
            guard key <= Self.maxKeyCode else { return }
            postKey(key, down: kind == .keyDown)
        }
    }

    private func displayPoint(x: Float, y: Float) -> CGPoint? {
        guard let displayID, x.isFinite, y.isFinite else { return nil }
        let bounds = CGDisplayBounds(displayID)
        guard bounds.width > 0, bounds.height > 0 else { return nil }
        // Keep the point inside the display (the last pixel is width - 1).
        let nx = CGFloat(min(max(x, 0), 1))
        let ny = CGFloat(min(max(y, 0), 1))
        return CGPoint(
            x: bounds.minX + min(nx * bounds.width, bounds.width - 1),
            y: bounds.minY + min(ny * bounds.height, bounds.height - 1))
    }

    // MARK: - The Mac's own screen beside the PC (on `queue`)
    //
    // With an extra display, the PC can push its mouse off the edge of its monitor that faces
    // the MacBook. The pointer then moves onto the Mac's own screen (the built-in display, or
    // another real display), entering from the side that faces the PC, and follows relative
    // moves there. Pushing back over that side hands the mouse back to the PC. This doesn't
    // depend on how the displays are arranged in System Settings.

    /// The display to use as "the Mac's own screen": the built-in one if it is on, otherwise
    /// another real display. Never the streamed display.
    private static func ownDisplay(excluding streamed: CGDirectDisplayID) -> CGDirectDisplayID? {
        var ids = [CGDirectDisplayID](repeating: 0, count: 16)
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(16, &ids, &count) == .success else { return nil }
        let others = ids.prefix(Int(count)).filter {
            $0 != streamed && CGDisplayMirrorsDisplay($0) == kCGNullDirectDisplay
        }
        return others.first(where: { CGDisplayIsBuiltin($0) != 0 })
            ?? others.first(where: { $0 == CGMainDisplayID() })
            ?? others.first
    }

    private func enterOwnScreen(side: OwnScreenSide, along: CGFloat) {
        guard let streamed = displayID, let own = Self.ownDisplay(excluding: streamed) else {
            if !loggedNoOwnScreen {
                loggedNoOwnScreen = true
                print("\(ServerText.mainTag) the PC's mouse can't move to this Mac's own screen: no other display is on (lid closed?)")
            }
            sendFeedback?(Data([Feedback.unavailable.rawValue]))
            return
        }
        let b = CGDisplayBounds(own)
        guard b.width > 1, b.height > 1 else {
            sendFeedback?(Data([Feedback.unavailable.rawValue]))
            return
        }
        // Enter from the side that faces the PC: a Mac on the PC's left is entered from its right.
        let point: CGPoint
        switch side {
        case .left:  point = CGPoint(x: b.maxX - 2, y: b.minY + along * (b.height - 1))
        case .right: point = CGPoint(x: b.minX + 1, y: b.minY + along * (b.height - 1))
        case .above: point = CGPoint(x: b.minX + along * (b.width - 1), y: b.maxY - 2)
        case .below: point = CGPoint(x: b.minX + along * (b.width - 1), y: b.minY + 1)
        }
        ownScreen = (own, side)
        postMove(to: point)
        sendFeedback?(Data([Feedback.entered.rawValue]))
        if !loggedOwnScreen {
            loggedOwnScreen = true
            print("\(ServerText.mainTag) the PC's mouse moved onto this Mac's own screen (display \(own))")
        }
    }

    private func moveOnOwnScreen(dx: CGFloat, dy: CGFloat) {
        guard let streamed = displayID else { return }
        guard let own = ownScreen else {
            // The PC thinks its mouse is on this Mac's own screen but it isn't (any more):
            // say where the pointer is, so the PC lets go of its cursor instead of holding it.
            let now = Date()
            guard now.timeIntervalSince(lastStrayFeedbackAt) >= 0.2 else { return }
            lastStrayFeedbackAt = now
            let s = CGDisplayBounds(streamed)
            guard s.width > 0, s.height > 0 else { return }
            let p = clamped(currentLocation(), to: s)
            leaveOwnScreen(.backOnStreamed, Float((p.x - s.minX) / s.width), Float((p.y - s.minY) / s.height))
            return
        }
        let b = CGDisplayBounds(own.id)
        guard b.width > 1, b.height > 1 else {
            leaveOwnScreen(.backAcrossEdge, 0.5, nil)   // the display went away
            return
        }
        let current = currentLocation()

        // Someone moved the pointer onto the streamed display meanwhile (the Mac's trackpad):
        // the PC takes over again from there.
        let s = CGDisplayBounds(streamed)
        if s.width > 0, s.height > 0, s.contains(current), !b.contains(current) {
            leaveOwnScreen(.backOnStreamed, Float((current.x - s.minX) / s.width),
                           Float((current.y - s.minY) / s.height))
            return
        }

        // The PC sends picture pixels; move by the same number of points it would move on
        // the streamed display (half as many on a Retina one), so the speed feels the same.
        let scale = Self.pointsPerPixel(streamed)
        let start = clamped(current, to: b)
        let x = start.x + dx * scale
        let y = start.y + dy * scale
        let alongY = Float((min(max(y, b.minY), b.maxY - 1) - b.minY) / (b.height - 1))
        let alongX = Float((min(max(x, b.minX), b.maxX - 1) - b.minX) / (b.width - 1))
        switch own.side {
        case .left where x > b.maxX - 1:
            leaveOwnScreen(.backAcrossEdge, alongY, nil)
        case .right where x < b.minX:
            leaveOwnScreen(.backAcrossEdge, alongY, nil)
        case .above where y > b.maxY - 1:
            leaveOwnScreen(.backAcrossEdge, alongX, nil)
        case .below where y < b.minY:
            leaveOwnScreen(.backAcrossEdge, alongX, nil)
        default:
            postMove(to: clamped(CGPoint(x: x, y: y), to: b))
        }
    }

    private func leaveOwnScreen(_ kind: Feedback, _ a: Float, _ b: Float?) {
        ownScreen = nil
        var data = Data([kind.rawValue])
        var values = [a]
        if let b { values.append(b) }
        for value in values {
            var bits = min(max(value, 0), 1).bitPattern.littleEndian
            withUnsafeBytes(of: &bits) { data.append(contentsOf: $0) }
        }
        sendFeedback?(data)
    }

    private static func pointsPerPixel(_ display: CGDirectDisplayID) -> CGFloat {
        guard let mode = CGDisplayCopyDisplayMode(display), mode.pixelWidth > 0 else { return 1 }
        let scale = CGDisplayBounds(display).width / CGFloat(mode.pixelWidth)
        return scale > 0 && scale <= 1 ? scale : 1
    }

    private func currentLocation() -> CGPoint {
        CGEvent(source: nil)?.location ?? lastPoint ?? .zero
    }

    private func clamped(_ p: CGPoint, to b: CGRect) -> CGPoint {
        CGPoint(x: min(max(p.x, b.minX), b.maxX - 1), y: min(max(p.y, b.minY), b.maxY - 1))
    }

    private func postMove(to point: CGPoint) {
        let type: CGEventType
        let button: CGMouseButton
        if heldButtons.contains(0) {
            type = .leftMouseDragged; button = .left
        } else if heldButtons.contains(1) {
            type = .rightMouseDragged; button = .right
        } else if heldButtons.contains(2) {
            type = .otherMouseDragged; button = .center
        } else {
            type = .mouseMoved; button = .left
        }
        if let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button) {
            event.flags = currentModifierFlags()
            post(event)
        }
        lastPoint = point
    }

    private func postButton(_ index: UInt8, down: Bool, clickCount: Int64, at point: CGPoint) {
        // Ignore an up for a button we never pressed, and a repeated down.
        if down && heldButtons.contains(index) { return }
        if !down && !heldButtons.contains(index) { return }

        let (type, button) = mouseEvent(for: index, down: down)
        guard let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button) else { return }
        event.setIntegerValueField(.mouseEventClickState, value: clickCount)
        if index == 2 { event.setIntegerValueField(.mouseEventButtonNumber, value: 2) }
        event.flags = currentModifierFlags()   // so Cmd-click, Shift-click etc. work
        post(event)

        if down { heldButtons.insert(index) } else { heldButtons.remove(index) }
        lastPoint = point
    }

    private func mouseEvent(for index: UInt8, down: Bool) -> (CGEventType, CGMouseButton) {
        switch index {
        case 1: return (down ? .rightMouseDown : .rightMouseUp, .right)
        case 2: return (down ? .otherMouseDown : .otherMouseUp, .center)
        default: return (down ? .leftMouseDown : .leftMouseUp, .left)
        }
    }

    private func postScroll(dx: Int32, dy: Int32) {
        guard dx != 0 || dy != 0 else { return }
        // CoreGraphics: wheel1 > 0 scrolls up, wheel2 > 0 scrolls left; the protocol uses +dx = right.
        if let event = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2, wheel1: dy, wheel2: -dx, wheel3: 0) {
            event.flags = currentModifierFlags()
            post(event)
        }
    }

    // MARK: - Keyboard (on `queue`)

    private func postKey(_ key: UInt16, down: Bool) {
        // A repeated down for a held key is the Windows key repeat: pass it on as an autorepeat
        // (injected keys do not auto-repeat on the Mac by themselves). An up for a key that is
        // not held is ignored.
        let isRepeat = down && heldKeys.contains(key)
        if !down && !heldKeys.contains(key) { return }

        let isModifier = Self.modifierFlags[key] != nil || key == Self.capsLockKey
        if isRepeat && isModifier { return }   // holding Shift etc. must not repeat

        if down { heldKeys.insert(key) } else { heldKeys.remove(key) }
        if key == Self.capsLockKey {
            if down { toggleCapsLock() }
            if capsLockViaSystem { return }   // macOS sends the Caps Lock change itself
        }

        guard let event = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(key), keyDown: down) else { return }
        if isModifier { event.type = .flagsChanged }
        var flags = currentModifierFlags()
        if Self.arrowKeys.contains(key) { flags.insert([.maskNumericPad, .maskSecondaryFn]) }
        if Self.keypadKeys.contains(key) { flags.insert(.maskNumericPad) }
        if Self.fnFlagKeys.contains(key) { flags.insert(.maskSecondaryFn) }
        event.flags = flags
        if isRepeat { event.setIntegerValueField(.keyboardEventAutorepeat, value: 1) }
        post(event)
    }

    /// The modifier flags implied by the keys the viewer is holding (plus Caps Lock).
    private func currentModifierFlags() -> CGEventFlags {
        var flags: CGEventFlags = []
        for key in heldKeys {
            if let flag = Self.modifierFlags[key] { flags.insert(flag) }
        }
        let capsOn = capsLockViaSystem
            ? CGEventSource.flagsState(.hidSystemState).contains(.maskAlphaShift)
            : capsLockOn
        if capsOn { flags.insert(.maskAlphaShift) }
        return flags
    }

    /// Caps Lock from the PC. Synthetic key events can't switch the Mac's real Caps Lock, so
    /// switch it through IOKit, the same as the Mac's own key (light, typing, every app). If
    /// macOS refuses, fall back to adding the Caps Lock flag to the keys typed from the PC.
    private func toggleCapsLock() {
        let wasOn = CGEventSource.flagsState(.hidSystemState).contains(.maskAlphaShift)
        if Self.setSystemCapsLock(!wasOn) {
            if !capsLockViaSystem { print("\(ServerText.mainTag) Caps Lock: switching the Mac's own Caps Lock") }
            capsLockViaSystem = true
            capsLockOn = !wasOn
        } else {
            if capsLockViaSystem || !loggedCapsFallback {
                print("\(ServerText.mainTag) Caps Lock: macOS refused the switch; using the Caps Lock flag on typed keys instead")
                loggedCapsFallback = true
            }
            capsLockViaSystem = false
            capsLockOn.toggle()
        }
    }

    private var loggedCapsFallback = false

    private static func setSystemCapsLock(_ on: Bool) -> Bool {
        // "IOHIDSystem" = kIOHIDSystemClass, 1 = kIOHIDParamConnectType, 1 = kIOHIDCapsLockState.
        let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOHIDSystem"))
        guard service != 0 else { return false }
        defer { IOObjectRelease(service) }
        var connect: io_connect_t = 0
        guard IOServiceOpen(service, mach_task_self_, 1, &connect) == KERN_SUCCESS else {
            return false
        }
        defer { IOServiceClose(connect) }
        return IOHIDSetModifierLockState(connect, 1, on) == KERN_SUCCESS
    }

    /// Releases every key the viewer pressed: ordinary keys first, then modifiers.
    private func releaseKeysLocked() {
        guard !heldKeys.isEmpty else { return }
        let keys = heldKeys.sorted { a, b in
            let aMod = Self.modifierFlags[a] != nil
            let bMod = Self.modifierFlags[b] != nil
            return aMod == bMod ? a < b : !aMod
        }
        for key in keys {
            postKey(key, down: false)
        }
        print("\(ServerText.mainTag) released held keys: \(keys.count)")
    }

    private func releaseAllLocked() {
        guard !heldButtons.isEmpty else { return }
        let point = lastPoint ?? CGEvent(source: nil)?.location ?? .zero
        for index in heldButtons.sorted() {
            let (type, button) = mouseEvent(for: index, down: false)
            if let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button) {
                if index == 2 { event.setIntegerValueField(.mouseEventButtonNumber, value: 2) }
                post(event)
            }
        }
        print("\(ServerText.mainTag) released held mouse buttons: \(heldButtons.sorted())")
        heldButtons.removeAll()
    }

    private func post(_ event: CGEvent?) {
        guard let event else { return }
        // Re-check the permission at most once a second (it can be granted while running).
        let now = Date()
        if now.timeIntervalSince(permissionCheckedAt) >= 1 {
            permissionCheckedAt = now
            hasPermission = CGPreflightPostEventAccess()
        }
        if !hasPermission {
            if !warnedNoPermission {
                warnedNoPermission = true
                print("\(ServerText.mainTag) mouse/keyboard input ignored: Macstream has no Accessibility permission (System Settings → Privacy & Security → Accessibility), then restart the server")
            }
            return
        }
        if !loggedFirstEvent {
            loggedFirstEvent = true
            print("\(ServerText.mainTag) receiving mouse input from the Windows viewer")
        }
        if !loggedFirstKey && (event.type == .keyDown || event.type == .keyUp || event.type == .flagsChanged) {
            loggedFirstKey = true
            print("\(ServerText.mainTag) receiving keyboard input from the Windows viewer")
        }
        event.post(tap: .cghidEventTap)
    }

    // MARK: - Watchdog

    private func startWatchdogIfNeeded() {
        guard watchdog == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.25, repeating: 0.25)
        timer.setEventHandler { [weak self] in
            guard let self, !self.heldButtons.isEmpty || !self.heldKeys.isEmpty else { return }
            if Date().timeIntervalSince(self.lastPacketAt) > Self.watchdogTimeout {
                print("\(ServerText.mainTag) no input from the viewer for \(Self.watchdogTimeout)s")
                self.releaseAllLocked()
                self.releaseKeysLocked()
            }
        }
        timer.resume()
        watchdog = timer
    }

    private func stopWatchdog() {
        watchdog?.cancel()
        watchdog = nil
    }

    // MARK: - Little-endian readers (callers have checked the length)

    private func readUInt32(_ b: [UInt8], _ o: Int) -> UInt32 {
        UInt32(b[o]) | (UInt32(b[o + 1]) << 8) | (UInt32(b[o + 2]) << 16) | (UInt32(b[o + 3]) << 24)
    }

    private func readFloat(_ b: [UInt8], _ o: Int) -> Float {
        Float(bitPattern: readUInt32(b, o))
    }

    private func readInt32(_ b: [UInt8], _ o: Int) -> Int32 {
        Int32(bitPattern: readUInt32(b, o))
    }

    private func clampScroll(_ v: Int32) -> Int32 {
        min(max(v, -Self.maxScrollPixels), Self.maxScrollPixels)
    }
}
