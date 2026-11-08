import AppKit
import Foundation
import CoreGraphics
import CoreVideo
import ServiceManagement

/// What is being streamed: the Mac's own screen, or an extra display of a given size. A viewer
/// asking for something else makes the server rebuild the stream (see onFirstClientConnected).
private struct StreamKey: Equatable {
    let extraDisplay: Bool
    let width: Int
    let height: Int
    let backingScale: Int
    let refreshRate: Int
    let fps: Int

    static let mirror = StreamKey(extraDisplay: false, width: 0, height: 0, backingScale: 0, refreshRate: 0, fps: 0)

    init(extraDisplay: Bool, width: Int, height: Int, backingScale: Int, refreshRate: Int, fps: Int) {
        self.extraDisplay = extraDisplay
        self.width = width
        self.height = height
        self.backingScale = backingScale
        self.refreshRate = refreshRate
        self.fps = fps
    }

    init(_ config: VirtualDisplayConfiguration, fps: Int) {
        if config.enabled {
            self.init(extraDisplay: true, width: config.width, height: config.height,
                      backingScale: config.backingScale, refreshRate: config.refreshRate, fps: fps)
        } else {
            self = .mirror
        }
    }
}

private final class StreamRuntime {
    let capture: ScreenCapture
    let encoder: H264Encoder?
    private(set) var virtualDisplay: VirtualDisplay?
    /// What the viewer asked for (kept even if the extra display failed and this mirrors).
    var key = StreamKey.mirror

    init(capture: ScreenCapture, encoder: H264Encoder?, virtualDisplay: VirtualDisplay?) {
        self.capture = capture
        self.encoder = encoder
        self.virtualDisplay = virtualDisplay
    }

    func stop() {
        capture.stop()
        encoder?.stop()
        virtualDisplay = nil   // releasing it removes the extra display from macOS
    }
}

@main
struct MacstreamServer {
    private static var h264PumpTimer: DispatchSourceTimer?
    private static var rawPumpTimer: DispatchSourceTimer?
    private static let runtimeQueue = DispatchQueue(label: "com.macstream.runtime")
    private static var activeRuntime: StreamRuntime?
    private static let inputInjector = MacInputInjector(
        enabled: windowsToMacInputEnabled(),
        keyboardEnabled: environmentValue("MACSTREAM_KEYBOARD") != "0")

    /// Windows -> Mac mouse/keyboard control policy.
    /// MACSTREAM_INPUT=0 turns it off, =1 forces it on. By default it is on only while the
    /// listener is restricted to wired interfaces (it is refused with MACSTREAM_INTERFACE=any,
    /// so nobody on Wi-Fi can control the Mac).
    private static func windowsToMacInputEnabled() -> Bool {
        if environmentValue("MACSTREAM_INPUT") == "0" { return false }
        if environmentValue("MACSTREAM_INPUT") == "1" { return true }
        return environmentValue("MACSTREAM_INTERFACE")?.lowercased() != "any"
    }

    static func main() {
        // tools/mac/uninstall.sh: remove the Start at Login entry, then exit.
        if CommandLine.arguments.contains("--unregister-login-item") {
            try? SMAppService.mainApp.unregister()
            print("Start at Login entry removed (if there was one)")
            exit(0)
        }
        applyFinderLaunchDefaults()

        // When launched as an app (tools/mac/run-server.sh), write logs to the file the
        // script follows. The app opens the file itself, so if the project lives in a
        // protected folder such as ~/Documents, macOS shows a normal access prompt for
        // Macstream instead of refusing to launch it.
        if let logPath = environmentValue("MACSTREAM_LOG_FILE"), !logPath.isEmpty {
            _ = freopen(logPath, "a", stdout)
            _ = freopen(logPath, "a", stderr)
        }
        // Line-buffer stdout so log lines appear immediately in the file.
        setvbuf(stdout, nil, _IOLBF, 0)

        ServerText.bannerLines.forEach { print($0) }
        print()
        if startedWithoutScript {
            print("\(ServerText.mainTag) started from Finder, the Dock or at login, log in \(environmentValue("MACSTREAM_LOG_FILE") ?? "-")")
        }

        // Ask for Screen Recording permission at launch instead of when the first
        // client connects, so the macOS prompt appears while the user is at the Mac.
        if !CGPreflightScreenCaptureAccess() {
            print("\(ServerText.mainTag) Screen Recording permission needed: allow Macstream in System Settings → Privacy & Security → Screen & System Audio Recording, then restart the server")
            _ = CGRequestScreenCaptureAccess()
        }

        if inputInjector.enabled {
            print(inputInjector.keyboardEnabled
                ? "\(ServerText.mainTag) Windows -> Mac mouse and keyboard control enabled (MACSTREAM_INPUT=0 disables both, MACSTREAM_KEYBOARD=0 only the keyboard)"
                : "\(ServerText.mainTag) Windows -> Mac mouse control enabled; keyboard disabled (MACSTREAM_KEYBOARD=0)")
            MacInputInjector.requestPermissionIfNeeded()
        } else {
            print("\(ServerText.mainTag) Windows -> Mac mouse control disabled")
        }

        let signalSource = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
        signal(SIGINT, SIG_IGN)

        let server = FrameServer(port: VideoPort)
        guard server.start() else {
            print("\(ServerText.mainTag) Failed to start server")
            return
        }

        print("\(ServerText.mainTag) Server started. Waiting for a Windows client before creating the Mac virtual display...")
        print("\(ServerText.mainTag) Video stream port: \(VideoPort)")
        print()

        let clipboard = ClipboardSync(
            allowed: environmentValue("MACSTREAM_CLIPBOARD") != "0",
            send: { server.sendClipboard($0) })

        // The menu-bar item needs AppKit's event loop, which runs at the end of main().
        let menuBar: MenuBarController?
        if environmentValue("MACSTREAM_MENU_BAR") != "0" {
            _ = NSApplication.shared   // MenuBarController sets the Dock icon on or off
            menuBar = MenuBarController(
                inputAvailable: inputInjector.enabled,
                logPath: environmentValue("MACSTREAM_LOG_FILE"),
                setInputPaused: { paused in
                    print("\(ServerText.mainTag) mouse and keyboard from the PC \(paused ? "paused" : "resumed") (menu bar)")
                    inputInjector.setPaused(paused)
                },
                pairingInfo: { server.pairingInfo() },
                forgetPairedClients: {
                    inputInjector.clientDisconnected()   // let go of anything the PC holds first
                    server.forgetPairedClients()
                },
                quit: { quitEverything(server: server, reason: "menu bar") })
        } else {
            menuBar = nil
        }
        clipboard.onStateChange = { on in menuBar?.setClipboardSync(on) }

        server.onFirstClientConnected = { clientInfo in
            menuBar?.setConnected(true)
            Task {
                // Keep the running stream if it shows what this viewer wants (the usual case:
                // a reconnect). If the viewer's Settings now ask for the other kind of screen
                // or another size, stop it and build the new one.
                let wantedFps = streamFPS(clientInfo: clientInfo)
                let wanted = StreamKey(VirtualDisplayConfiguration.load(
                    clientInfo: clientInfo, fps: wantedFps), fps: wantedFps)
                if let current = activeRuntimeKey() {
                    if current == wanted { return }
                    print("\(ServerText.mainTag) The PC asked for a different Mac screen; rebuilding the stream")
                    takeActiveRuntime()?.stop()
                    server.clearCachedFrame()
                    // Give macOS a moment to remove an extra display and move windows back
                    // before the next display is looked up.
                    try? await Task.sleep(nanoseconds: 700_000_000)
                }

                print("\(ServerText.mainTag) Client connected. Preparing screen capture...")
                guard let runtime = await configureAndStartStreaming(server: server, clientInfo: clientInfo) else {
                    return
                }
                runtime.key = wanted

                if !installRuntimeIfNeeded(runtime) {
                    runtime.stop()
                    return
                }
                // The PC may have gone away while this was being built.
                if server.clientCount == 0 {
                    scheduleExtraDisplayRelease(server: server, menuBar: menuBar)
                }
                let showing: String
                if runtime.virtualDisplay != nil {
                    showing = "Showing: an extra display, \(wanted.width)×\(wanted.height)\(wanted.backingScale > 1 ? " Retina" : "")"
                } else if wanted.extraDisplay {
                    showing = "Showing: this Mac’s screen (the extra display failed, see the log)"
                } else {
                    showing = "Showing: this Mac’s screen"
                }
                DispatchQueue.main.async { menuBar?.setShowing(showing) }
            }
        }
        server.onInputEvent = { payload in
            inputInjector.handle(payload: payload)
        }
        inputInjector.sendFeedback = { server.sendInputFeedback($0) }
        server.onClipboard = { payload in
            clipboard.handle(payload: payload)
        }
        server.onLastClientDisconnected = {
            inputInjector.clientDisconnected()
            clipboard.clientDisconnected()
            menuBar?.setConnected(false)
            scheduleExtraDisplayRelease(server: server, menuBar: menuBar)
        }

        signalSource.setEventHandler {
            quitEverything(server: server, reason: "Ctrl+C")
        }
        signalSource.resume()

        // Keep running: AppKit's event loop also serves the main dispatch queue (signal
        // handler, menu bar). Without the menu bar, just serve the main queue as before.
        if menuBar != nil {
            NSApplication.shared.run()
        } else {
            dispatchMain()
        }
    }

    /// Started from Finder, the Dock or at login rather than by tools/mac/run-server.sh (which
    /// always sets MACSTREAM_LOG_FILE): use the same defaults the script sets, i.e. mirror
    /// the Mac's own display and log to <project>/logs/mac-server.log. The previous log is
    /// kept as mac-server.previous.log. Must run before anything reads these variables.
    private static var startedWithoutScript = false

    private static func applyFinderLaunchDefaults() {
        guard environmentValue("MACSTREAM_LOG_FILE") == nil else { return }
        startedWithoutScript = true

        // The app lives in <project>/build/mac/Macstream.app.
        let project = Bundle.main.bundleURL
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let fm = FileManager.default
        let logs: URL
        if fm.fileExists(atPath: project.appendingPathComponent("tools/mac/run-server.sh").path) {
            logs = project.appendingPathComponent("logs")
        } else {
            logs = fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/Macstream")
        }
        try? fm.createDirectory(at: logs, withIntermediateDirectories: true)
        let log = logs.appendingPathComponent("mac-server.log")
        let previous = logs.appendingPathComponent("mac-server.previous.log")
        if fm.fileExists(atPath: log.path) {
            try? fm.removeItem(at: previous)
            try? fm.moveItem(at: log, to: previous)
        }
        setenv("MACSTREAM_LOG_FILE", log.path, 1)
    }

    /// Releases every key and button the PC holds on this Mac, stops capture and the server,
    /// and exits. run-server.sh notices and stops its log window too.
    private static func quitEverything(server: FrameServer, reason: String) {
        print("\n\(ServerText.mainTag) Shutting down (\(reason))...")
        inputInjector.shutdown()
        let runtime = takeActiveRuntime()
        runtime?.stop()
        server.stop()
        exit(0)
    }

    /// When the PC disconnects, remove the extra display (if that is what was streamed) so the
    /// Mac doesn't keep an invisible screen that windows and the mouse can wander onto. A short
    /// wait first lets a quick reconnect (cable blip, viewer restart) keep the same display and
    /// the windows on it. The mirror keeps running as before, so reconnecting to it stays instant.
    private static let extraDisplayReleaseDelay: TimeInterval = 5
    private static var releaseGeneration = 0   // guarded by runtimeQueue

    private static func scheduleExtraDisplayRelease(server: FrameServer, menuBar: MenuBarController?) {
        // Called on the server queue: only hop to runtimeQueue asynchronously, because the
        // release below asks the server queue for the client count.
        runtimeQueue.async {
            releaseGeneration += 1
            let generation = releaseGeneration
            runtimeQueue.asyncAfter(deadline: .now() + extraDisplayReleaseDelay) {
                // Runs on runtimeQueue, so a viewer connecting now waits for this before it
                // looks at the running stream, and then builds a new one.
                guard generation == releaseGeneration,
                      server.clientCount == 0,
                      let runtime = activeRuntime,
                      runtime.virtualDisplay != nil else { return }
                activeRuntime = nil
                print("\(ServerText.mainTag) PC disconnected; removing the extra Mac display")
                runtime.stop()
                server.clearCachedFrame()
                DispatchQueue.main.async { menuBar?.setShowing(nil) }
            }
        }
    }

    private static func activeRuntimeKey() -> StreamKey? {
        runtimeQueue.sync {
            activeRuntime?.key
        }
    }

    private static func installRuntimeIfNeeded(_ runtime: StreamRuntime) -> Bool {
        runtimeQueue.sync {
            guard activeRuntime == nil else { return false }
            activeRuntime = runtime
            return true
        }
    }

    private static func takeActiveRuntime() -> StreamRuntime? {
        runtimeQueue.sync {
            let runtime = activeRuntime
            activeRuntime = nil
            return runtime
        }
    }

    private static func configureAndStartStreaming(server: FrameServer, clientInfo: ClientDisplayInfo?) async -> StreamRuntime? {
        let streamFps = streamFPS(clientInfo: clientInfo)
        let virtualConfig = VirtualDisplayConfiguration.load(clientInfo: clientInfo, fps: streamFps)

        let virtualDisplay: VirtualDisplay?
        let displayID: CGDirectDisplayID
        let fallbackResolution: (width: Int, height: Int)?

        var created: VirtualDisplay?
        if virtualConfig.enabled {
            created = VirtualDisplay.create(configuration: virtualConfig)
            if created == nil {
                // macOS may still be removing the previous extra display: try once more.
                print("\(ServerText.mainTag) The extra display could not be made; trying again in a second")
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                created = VirtualDisplay.create(configuration: virtualConfig)
            }
        }

        if virtualConfig.enabled, let createdDisplay = created {
            virtualDisplay = createdDisplay
            displayID = createdDisplay.displayID
            fallbackResolution = (virtualConfig.width, virtualConfig.height)
            print("\(ServerText.mainTag) Using an extra Mac display for the PC (Windows app: Settings > Mac screen)")
        } else {
            if virtualConfig.enabled {
                print("\(ServerText.mainTag) The extra display could not be created; mirroring this Mac's screen instead")
            }
            virtualDisplay = nil
            displayID = CGMainDisplayID()
            fallbackResolution = nil
            print("\(ServerText.mainTag) Using main-display mirror mode")
        }

        let (width, height) = displayResolution(displayID: displayID, fallback: fallbackResolution)
        guard width > 0, height > 0 else {
            print("\(ServerText.mainTag) Failed to get display resolution")
            return nil
        }

        print("\(ServerText.mainTag) Capture display: \(displayID), resolution: \(width)x\(height), FPS: \(streamFps)")

        let capture = ScreenCapture(displayID: displayID, fps: streamFps)
        let encoder = H264Encoder(width: width, height: height, fps: streamFps)
        let forceRaw = environmentValue("MACSTREAM_RAW") == "1"
        let runtime: StreamRuntime

        if !forceRaw && encoder.start() {
            print("\(ServerText.mainTag) \(encoder.codecName) encode mode")
            capture.preferYUV = ScreenCapture.yuvCaptureEnabled
            startH264Mode(capture: capture, encoder: encoder, server: server, width: width, height: height, fps: streamFps,
                          extraDisplay: virtualDisplay != nil)
            server.onKeyFrameNeeded = { [weak encoder] in encoder?.requestKeyFrame() }
            runtime = StreamRuntime(capture: capture, encoder: encoder, virtualDisplay: virtualDisplay)
        } else {
            if !forceRaw && !encoder.usesDefaultCodec && environmentValue("MACSTREAM_ALLOW_RAW_FALLBACK") != "1" {
                print("\(ServerText.mainTag) \(encoder.codecName) encoder failed to start and the server stopped. Set MACSTREAM_ALLOW_RAW_FALLBACK=1 to allow RAW fallback.")
                return nil
            }
            print(forceRaw
                ? "\(ServerText.mainTag) Forced RAW mode enabled"
                : "\(ServerText.mainTag) H.264 encoder failed to start; using RAW mode")
            startRawMode(capture: capture, server: server, width: width, height: height, fps: streamFps)
            runtime = StreamRuntime(capture: capture, encoder: nil, virtualDisplay: virtualDisplay)
        }

        inputInjector.setTargetDisplay(displayID)
        print("\(ServerText.mainTag) Starting screen capture...")
        await capture.start()
        return runtime
    }

    private static func envBool(_ key: String, defaultValue: Bool) -> Bool {
        guard let value = environmentValue(key)?.lowercased() else {
            return defaultValue
        }

        switch value {
        case "1", "true", "yes", "on":
            return true
        case "0", "false", "no", "off":
            return false
        default:
            return defaultValue
        }
    }

    static func displayResolution(displayID: CGDirectDisplayID, fallback: (width: Int, height: Int)? = nil) -> (width: Int, height: Int) {
        let scale = captureScale()

        if let size = CaptureSize.pixels(for: displayID, scale: scale) {
            print("\(ServerText.mainTag) Capture size: \(size.description)")
            return (size.width, size.height)
        }

        let displayWidth = CGDisplayPixelsWide(displayID)
        let displayHeight = CGDisplayPixelsHigh(displayID)
        if displayWidth > 0, displayHeight > 0 {
            return (
                alignVideoDimension(Int(Double(displayWidth) * scale)),
                alignVideoDimension(Int(Double(displayHeight) * scale))
            )
        }

        if let fallback {
            return (
                alignVideoDimension(Int(Double(fallback.width) * scale)),
                alignVideoDimension(Int(Double(fallback.height) * scale))
            )
        }

        return (0, 0)
    }

    static func captureScale() -> Double {
        if let value = environmentValue("MACSTREAM_SCALE"),
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            return parsed
        }

        return 1
    }

    static func streamFPS(clientInfo: ClientDisplayInfo? = nil) -> Int {
        if let value = environmentValue("MACSTREAM_FPS"),
           let parsed = Int(value),
           parsed > 0,
           parsed <= 120 {
            return parsed
        }

        if let refreshRate = clientInfo?.refreshRate,
           refreshRate >= 24 {
            // Above 60 only for an extra display whose viewer picked a higher frame rate (90 or
            // 120 in its Settings); otherwise (and always for the mirror) at most 60, as before.
            let requested = clientInfo?.screen?.maxFps ?? 0
            let cap = requested > 60 && VirtualDisplayConfiguration.wantsExtraDisplay(clientInfo: clientInfo)
                ? min(requested, 120) : 60
            return min(refreshRate, cap)
        }

        return 60
    }

    static func startH264Mode(capture: ScreenCapture, encoder: H264Encoder, server: FrameServer, width: Int, height: Int, fps: Int,
                              extraDisplay: Bool = false) {
        var frameCount: UInt64 = 0
        var inputFrameCount: UInt64 = 0
        var lastReportTime = Date()
        var lastReportFrame: UInt64 = 0
        var lastInputReportTime = Date()
        var lastInputReportFrame: UInt64 = 0
        var lastInputHash: UInt64 = 0
        let pendingFrameLock = NSLock()
        let encoderQueue = DispatchQueue(label: "com.macstream.h264-encoder", qos: .userInteractive)
        var latestPixelBuffer: CVPixelBuffer?
        var latestTimestamp: UInt64 = 0
        var encoderScheduled = false
        var skippedForBackpressure: UInt64 = 0
        var lastBackpressureReportTime = Date()
        let hashDiagnosticsEnabled = envBool("MACSTREAM_DIAG_HASH", defaultValue: false)
        let stats = StreamStats()

        // Once a second, send the Mac-side numbers to the client (shown in its status bar).
        h264PumpTimer?.cancel()
        let statsTimer = DispatchSource.makeTimerSource(queue: DispatchQueue(label: "com.macstream.stats"))
        statsTimer.schedule(deadline: .now() + 1, repeating: 1.0)
        statsTimer.setEventHandler {
            let dropped = server.takeDroppedFrameCount()
            let payload = stats.takePayload(
                width: width,
                height: height,
                isHEVC: encoder.packetType == .frameHevc,
                targetBitRate: encoder.targetBitRate,
                droppedFrames: dropped,
                maxQP: encoder.maxFrameQP,
                lowLatency: encoder.lowLatencyMode,
                yuvCapture: capture.preferYUV,
                extraDisplay: extraDisplay)
            guard server.clientCount > 0 else { return }
            server.sendRealtime(data: payload, packetType: .stats)
        }
        h264PumpTimer = statsTimer
        statsTimer.resume()

        func scheduleEncoderIfNeeded() {
            pendingFrameLock.lock()
            if encoderScheduled {
                pendingFrameLock.unlock()
                return
            }
            encoderScheduled = true
            pendingFrameLock.unlock()

            encoderQueue.async {
                while true {
                    pendingFrameLock.lock()
                    guard let pixelBuffer = latestPixelBuffer else {
                        encoderScheduled = false
                        pendingFrameLock.unlock()
                        return
                    }
                    let timestamp = latestTimestamp
                    latestPixelBuffer = nil
                    pendingFrameLock.unlock()

                    guard server.clientCount > 0 else { continue }
                    if server.hasFrameBacklog {
                        skippedForBackpressure += 1
                        stats.addSkipped()
                        let now = Date()
                        if now.timeIntervalSince(lastBackpressureReportTime) >= 1.0 {
                            print("\(ServerText.encoderTag) Network send is still pending; deferred encode inputs: \(skippedForBackpressure)")
                            skippedForBackpressure = 0
                            lastBackpressureReportTime = now
                        }
                        // Don't discard this image: if it is the last change on screen (end of a
                        // scroll, a typed character) nothing newer would follow and the viewer would
                        // be stuck on an older picture. Keep it unless a newer one already arrived,
                        // and retry shortly.
                        pendingFrameLock.lock()
                        if latestPixelBuffer == nil {
                            latestPixelBuffer = pixelBuffer
                            latestTimestamp = timestamp
                        }
                        encoderScheduled = false
                        pendingFrameLock.unlock()
                        encoderQueue.asyncAfter(deadline: .now() + .milliseconds(2)) {
                            scheduleEncoderIfNeeded()
                        }
                        return
                    }
                    encoder.encode(pixelBuffer: pixelBuffer, timestamp: timestamp)
                }
            }
        }

        encoder.onNALUnit = { nalData, isKeyFrame, _, captureTimestamp in
            frameCount += 1

            // Frame data: [8 bytes frameIndex] [8 bytes timestamp] [1 byte isKeyFrame] [4 bytes width] [4 bytes height] [NAL data]
            var payload = Data()
            var idx = frameCount.littleEndian
            var ts = captureTimestamp.littleEndian
            var kf: UInt8 = isKeyFrame ? 1 : 0
            var w = UInt32(width).littleEndian
            var h = UInt32(height).littleEndian

            payload.append(Data(bytes: &idx, count: 8))
            payload.append(Data(bytes: &ts, count: 8))
            payload.append(Data(bytes: &kf, count: 1))
            payload.append(Data(bytes: &w, count: 4))
            payload.append(Data(bytes: &h, count: 4))
            payload.append(nalData)

            server.sendFrame(data: payload, packetType: encoder.packetType, isKeyFrame: isKeyFrame, cacheForNewClients: isKeyFrame)
            let nowMs = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
            stats.addEncoded(bytes: nalData.count, latencyMs: captureTimestamp > 0 ? Double(nowMs &- captureTimestamp) : 0, encoderMs: encoder.lastEncodeDurationMs)

            let now = Date()
            if now.timeIntervalSince(lastReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastReportTime)
                let fps = Double(frameCount - lastReportFrame) / elapsed
                let sizeKB = Double(nalData.count) / 1024.0
                print("\(ServerText.statsTag) FPS: \(String(format: "%.1f", fps)), frame size: \(String(format: "%.1f", sizeKB)) KB, key frame: \(isKeyFrame), clients: \(server.clientCount)")
                lastReportTime = now
                lastReportFrame = frameCount
            }
        }

        capture.onFrame = { pixelBuffer, _ in
            guard server.clientCount > 0 else { return }

            inputFrameCount += 1
            stats.addCaptured()
            let timestamp = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
            pendingFrameLock.lock()
            latestPixelBuffer = pixelBuffer
            latestTimestamp = timestamp
            pendingFrameLock.unlock()
            scheduleEncoderIfNeeded()

            let now = Date()
            if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastInputReportTime)
                let fps = Double(inputFrameCount - lastInputReportFrame) / elapsed
                if hashDiagnosticsEnabled {
                    let hash = pixelBufferSampleHash(pixelBuffer)
                    let changed = hash != lastInputHash
                    print("\(ServerText.captureStatsTag) input FPS: \(String(format: "%.1f", fps)), hash: \(String(hash, radix: 16)), changed: \(changed), clients: \(server.clientCount)")
                    lastInputHash = hash
                } else {
                    print("\(ServerText.captureStatsTag) input FPS: \(String(format: "%.1f", fps)), clients: \(server.clientCount)")
                }
                lastInputReportTime = now
                lastInputReportFrame = inputFrameCount
            }
        }
    }

    static func startRawMode(capture: ScreenCapture, server: FrameServer, width: Int, height: Int, fps: Int) {
        var frameCount: UInt64 = 0
        var lastReportTime = Date()
        var lastReportFrame: UInt64 = 0
        var inputFrameCount: UInt64 = 0
        var lastInputReportTime = Date()
        var lastInputReportFrame: UInt64 = 0
        var lastInputHash: UInt64 = 0
        let hashDiagnosticsEnabled = envBool("MACSTREAM_DIAG_HASH", defaultValue: false)
        let latestFrameLock = NSLock()
        var latestPixelBuffer: CVPixelBuffer?

        capture.onFrame = { pixelBuffer, _ in
            inputFrameCount += 1
            latestFrameLock.lock()
            latestPixelBuffer = pixelBuffer
            latestFrameLock.unlock()

            let now = Date()
            if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastInputReportTime)
                let fps = Double(inputFrameCount - lastInputReportFrame) / elapsed
                if hashDiagnosticsEnabled {
                    let hash = pixelBufferSampleHash(pixelBuffer)
                    let changed = hash != lastInputHash
                    print("\(ServerText.rawCaptureStatsTag) input FPS: \(String(format: "%.1f", fps)), hash: \(String(hash, radix: 16)), changed: \(changed), clients: \(server.clientCount)")
                    lastInputHash = hash
                } else {
                    print("\(ServerText.rawCaptureStatsTag) input FPS: \(String(format: "%.1f", fps)), clients: \(server.clientCount)")
                }
                lastInputReportTime = now
                lastInputReportFrame = inputFrameCount
            }
        }

        let pumpQueue = DispatchQueue(label: "com.macstream.raw-pump", qos: .userInteractive)
        let pumpTimer = DispatchSource.makeTimerSource(queue: pumpQueue)
        pumpTimer.schedule(deadline: .now(), repeating: 1.0 / Double(fps), leeway: .milliseconds(2))
        pumpTimer.setEventHandler {
            latestFrameLock.lock()
            let pixelBuffer = latestPixelBuffer
            latestFrameLock.unlock()

            guard server.clientCount > 0, let pixelBuffer else { return }

            frameCount += 1

            CVPixelBufferLockBaseAddress(pixelBuffer, [])
            defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

            guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
            let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
            let dataSize = bytesPerRow * height
            let data = Data(bytes: baseAddress, count: dataSize)

            var payload = Data()
            var idx = frameCount.littleEndian
            var ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000).littleEndian
            var w = UInt32(width).littleEndian
            var h = UInt32(height).littleEndian
            var stride = UInt32(bytesPerRow).littleEndian

            payload.append(Data(bytes: &idx, count: 8))
            payload.append(Data(bytes: &ts, count: 8))
            payload.append(Data(bytes: &w, count: 4))
            payload.append(Data(bytes: &h, count: 4))
            payload.append(Data(bytes: &stride, count: 4))
            payload.append(data)

            server.sendFrame(data: payload, packetType: .frameRaw, isKeyFrame: true)

            let now = Date()
            if now.timeIntervalSince(lastReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastReportTime)
                let fps = Double(frameCount - lastReportFrame) / elapsed
                let sizeMB = Double(data.count) / (1024.0 * 1024.0)
                print("\(ServerText.rawStatsTag) FPS: \(String(format: "%.1f", fps)), frame size: \(String(format: "%.1f", sizeMB)) MB, clients: \(server.clientCount)")
                lastReportTime = now
                lastReportFrame = frameCount
            }
        }
        rawPumpTimer = pumpTimer
        pumpTimer.resume()
    }

    static func pixelBufferSampleHash(_ pixelBuffer: CVPixelBuffer) -> UInt64 {
        // Diagnostic only; assumes packed 4-byte pixels.
        guard CVPixelBufferGetPixelFormatType(pixelBuffer) == kCVPixelFormatType_32BGRA else { return 0 }
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

        guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else { return 0 }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let bytes = baseAddress.assumingMemoryBound(to: UInt8.self)

        var hash: UInt64 = 1469598103934665603
        let sampleRows = 8
        let sampleCols = 8

        for rowIndex in 0..<sampleRows {
            let y = min(height - 1, rowIndex * max(1, height / sampleRows))
            for colIndex in 0..<sampleCols {
                let x = min(width - 1, colIndex * max(1, width / sampleCols))
                let offset = y * bytesPerRow + x * 4
                for i in 0..<4 {
                    hash ^= UInt64(bytes[offset + i])
                    hash &*= 1099511628211
                }
            }
        }

        return hash
    }
}

/// Reads a setting from the environment with getenv(), so values set at start-up with setenv()
/// (see applyFinderLaunchDefaults) are always seen. Settings are named MACSTREAM_*; the older
/// WIRED_MONITOR_* name of the same setting is still read when the new one isn't set.
func environmentValue(_ name: String) -> String? {
    if let value = getenv(name) { return String(cString: value) }
    let prefix = "MACSTREAM_"
    if name.hasPrefix(prefix),
       let value = getenv("WIRED_MONITOR_" + String(name.dropFirst(prefix.count))) {
        return String(cString: value)
    }
    return nil
}
