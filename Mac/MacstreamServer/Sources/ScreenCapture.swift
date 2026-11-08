import Foundation
import CoreGraphics
import CoreVideo
import IOSurface
import ScreenCaptureKit

class ScreenCapture {
    private let displayID: CGDirectDisplayID
    private let fps: Int
    private let scale: Double
    private let includeCursor: Bool

    /// Capture straight into 4:2:0 YUV (video range, BT.709) instead of BGRA. The encoder works in
    /// YUV, so this skips a per-frame colour conversion inside VideoToolbox and halves the pixel
    /// data, with WindowServer converting on the GPU while it composites. Set by Main for the
    /// encoded (H.264/HEVC) path only; RAW mode needs BGRA. MACSTREAM_CAPTURE_FORMAT=bgra
    /// turns it off.
    var preferYUV = false
    private var captureFormat: OSType {
        preferYUV ? kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange : kCVPixelFormatType_32BGRA
    }

    static var yuvCaptureEnabled: Bool {
        environmentValue("MACSTREAM_CAPTURE_FORMAT")?.lowercased() != "bgra"
    }
    private var isRunning = false
    private var stream: SCStream?
    private var streamOutput: StreamOutput?
    private var displayStream: CGDisplayStream?
    private var fallbackTimer: DispatchSourceTimer?
    private var fallbackPixelBuffer: CVPixelBuffer?
    private var fallbackContext: CGContext?
    private var fallbackBaseAddress: UnsafeMutableRawPointer?
    private let fallbackColorSpace = CGColorSpaceCreateDeviceRGB()

    private(set) var width: Int = 0
    private(set) var height: Int = 0

    var onFrame: ((CVPixelBuffer, UInt64) -> Void)?

    init(displayID: CGDirectDisplayID = CGMainDisplayID(), fps: Int = 60) {
        self.displayID = displayID
        self.fps = fps
        if let value = environmentValue("MACSTREAM_SCALE"),
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            scale = parsed
        } else {
            scale = 1
        }
        includeCursor = Self.envBool("MACSTREAM_CAPTURE_CURSOR", defaultValue: true)
    }

    var resolution: (width: Int, height: Int) {
        (width, height)
    }

    /// How many captured frames may be in use at once. With 1, macOS can't deliver the next frame
    /// while the encoder still holds the previous one (~20-30 ms), which capped capture at ~50 fps.
    /// MACSTREAM_QUEUE_DEPTH (1-8), default 4.
    private static func captureQueueDepth() -> Int {
        if let value = environmentValue("MACSTREAM_QUEUE_DEPTH"),
           let parsed = Int(value), (1...8).contains(parsed) {
            return parsed
        }
        return 4
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

    func start() async {
        guard !isRunning else { return }
        isRunning = true

        let captureMode = environmentValue("MACSTREAM_CAPTURE")?.lowercased()
        if captureMode == "image" || captureMode == "cgimage" {
            print("\(ServerText.captureTag) Forcing CGDisplayCreateImage active capture mode")
            isRunning = false
            await startFallback()
            return
        }

        if captureMode == nil ||
            captureMode == "cgstream" ||
            captureMode == "displaystream" ||
            environmentValue("MACSTREAM_ACTIVE_CAPTURE") == "1" {
            print(captureMode == nil
                ? "\(ServerText.captureTag) Using CGDisplayStream active capture mode by default"
                : "\(ServerText.captureTag) Forcing CGDisplayStream active capture mode")
            startDisplayStream()
            return
        }

        if captureMode != "sck" && captureMode != "screencapturekit" {
            print("\(ServerText.captureTag) Unknown capture mode \(captureMode ?? ""), available: cgstream/sck/image")
            isRunning = false
            return
        }

        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            DisplayDiagnostics.printShareableDisplays(content.displays, target: displayID, prefix: ServerText.captureTag)

            guard let display = content.displays.first(where: { $0.displayID == displayID }) else {
                print("\(ServerText.captureTag) ScreenCaptureKit could not find target display displayID=\(displayID), refusing to capture another display")
                isRunning = false
                return
            }

            // Panel-native pixels by default (see CaptureSize.swift)
            if let size = CaptureSize.pixels(for: display.displayID, scale: scale) {
                width = size.width
                height = size.height
            } else {
                width = alignVideoDimension(Int(Double(display.width) * scale))
                height = alignVideoDimension(Int(Double(display.height) * scale))
            }

            let filter = SCContentFilter(display: display, excludingWindows: [])

            let config = SCStreamConfiguration()
            config.width = width
            config.height = height
            config.pixelFormat = captureFormat
            if preferYUV {
                config.colorMatrix = CGDisplayStream.yCbCrMatrix_ITU_R_709_2
            }
            config.minimumFrameInterval = CMTime(value: 1, timescale: Int32(fps))
            config.queueDepth = Self.captureQueueDepth()
            config.showsCursor = includeCursor
            config.capturesAudio = false

            let output = StreamOutput(capture: self)
            streamOutput = output

            stream = SCStream(filter: filter, configuration: config, delegate: nil)
            try stream!.addStreamOutput(output, type: .screen, sampleHandlerQueue: DispatchQueue(label: "com.macstream.sck", qos: .userInteractive))

            try await stream!.startCapture()

            print("\(ServerText.captureTag) ScreenCaptureKit started displayID=\(display.displayID), \(width)x\(height) @ \(fps)fps, cursor=\(includeCursor)")
        } catch {
            print("\(ServerText.captureTag) ScreenCaptureKit failed to start: \(error)")
            if environmentValue("MACSTREAM_ALLOW_CAPTURE_FALLBACK") == "1" {
                print("\(ServerText.captureTag) Fallback to CGDisplayCreateImage is allowed")
                isRunning = false
                await startFallback()
            } else {
                print("\(ServerText.captureTag) MACSTREAM_ALLOW_CAPTURE_FALLBACK is not enabled; stopped to avoid capturing the wrong display")
                isRunning = false
            }
        }
    }

    func stop() {
        isRunning = false
        if let stream = stream {
            Task { try? await stream.stopCapture() }
        }
        fallbackTimer?.cancel()
        fallbackTimer = nil
        if let displayStream = displayStream {
            _ = displayStream.stop()
        }
        displayStream = nil
        stream = nil
        streamOutput = nil
        fallbackContext = nil
        fallbackPixelBuffer = nil
        fallbackBaseAddress = nil
        print("\(ServerText.captureTag) stopped")
    }

    private func startDisplayStream() {
        isRunning = true
        DisplayDiagnostics.printOnlineDisplays(prefix: ServerText.captureTag)

        if let size = CaptureSize.pixels(for: displayID, scale: scale) {
            width = size.width
            height = size.height
        } else {
            width = alignVideoDimension(Int(Double(CGDisplayPixelsWide(displayID)) * scale))
            height = alignVideoDimension(Int(Double(CGDisplayPixelsHigh(displayID)) * scale))
        }

        let queue = DispatchQueue(label: "com.macstream.cgdisplaystream", qos: .userInteractive)
        var properties: [CFString: Any] = [
            CGDisplayStream.showCursor: includeCursor,
            CGDisplayStream.queueDepth: Self.captureQueueDepth(),
            CGDisplayStream.minimumFrameTime: 1.0 / Double(fps),
        ]

        if preferYUV {
            properties[CGDisplayStream.yCbCrMatrix] = CGDisplayStream.yCbCrMatrix_ITU_R_709_2
        }

        let pixelFormat = Int32(bitPattern: captureFormat)
        guard let stream = CGDisplayStream(
            dispatchQueueDisplay: displayID,
            outputWidth: width,
            outputHeight: height,
            pixelFormat: pixelFormat,
            properties: properties as CFDictionary,
            queue: queue,
            handler: { [weak self] status, displayTime, surface, update in
                self?.handleDisplayStreamFrame(status: status, displayTime: displayTime, surface: surface)
            })
        else {
            print("\(ServerText.captureTag) CGDisplayStream creation failed displayID=\(displayID)")
            if environmentValue("MACSTREAM_ALLOW_CAPTURE_FALLBACK") == "1" {
                print("\(ServerText.captureTag) Fallback to CGDisplayCreateImage is allowed")
                isRunning = false
                Task { await self.startFallback() }
            } else {
                print("\(ServerText.captureTag) MACSTREAM_ALLOW_CAPTURE_FALLBACK is not enabled; stopped to avoid capturing the wrong display")
                isRunning = false
            }
            return
        }

        let error = stream.start()
        guard error == .success else {
            print("\(ServerText.captureTag) CGDisplayStream failed to start displayID=\(displayID): \(error.rawValue)")
            _ = stream.stop()
            if environmentValue("MACSTREAM_ALLOW_CAPTURE_FALLBACK") == "1" {
                print("\(ServerText.captureTag) Fallback to CGDisplayCreateImage is allowed")
                isRunning = false
                Task { await self.startFallback() }
            } else {
                print("\(ServerText.captureTag) MACSTREAM_ALLOW_CAPTURE_FALLBACK is not enabled; stopped to avoid capturing the wrong display")
                isRunning = false
            }
            return
        }

        displayStream = stream
        print("\(ServerText.captureTag) CGDisplayStream started displayID=\(displayID), \(width)x\(height) @ \(fps)fps, cursor=\(includeCursor)")
    }

    private func handleDisplayStreamFrame(status: CGDisplayStreamFrameStatus, displayTime: UInt64, surface: IOSurface?) {
        guard isRunning else { return }
        guard status == .frameComplete else { return }
        guard let surface else { return }

        var unmanagedPixelBuffer: Unmanaged<CVPixelBuffer>?
        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: captureFormat,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:],
        ]
        let result = CVPixelBufferCreateWithIOSurface(
            kCFAllocatorDefault,
            surface,
            attributes as CFDictionary,
            &unmanagedPixelBuffer)

        guard result == kCVReturnSuccess, let unmanagedPixelBuffer else {
            print("\(ServerText.captureTag) IOSurface to CVPixelBuffer failed: \(result)")
            return
        }

        let pixelBuffer = unmanagedPixelBuffer.takeRetainedValue()
        let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
        onFrame?(pixelBuffer, ts)
    }

    // Fallback: use CGDisplayCreateImage
    private func startFallback() async {
        isRunning = true

        if let size = CaptureSize.pixels(for: displayID, scale: scale) {
            width = size.width
            height = size.height
        } else {
            width = alignVideoDimension(Int(Double(CGDisplayPixelsWide(displayID)) * scale))
            height = alignVideoDimension(Int(Double(CGDisplayPixelsHigh(displayID)) * scale))
        }

        print("\(ServerText.captureTag) CGDisplay fallback mode \(width)x\(height)")
        prepareFallbackBuffers()

        let interval = 1.0 / Double(fps)
        let queue = DispatchQueue(label: "com.macstream.capture", qos: .userInteractive)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: interval, leeway: .milliseconds(1))
        timer.setEventHandler { [weak self] in
            self?.captureFallbackFrame()
        }
        timer.resume()
        fallbackTimer = timer
    }

    private func captureFallbackFrame() {
        guard isRunning else { return }

        let rect = CGRect(x: 0, y: 0, width: CGFloat(CGDisplayPixelsWide(displayID)),
                          height: CGFloat(CGDisplayPixelsHigh(displayID)))
        guard let image = CGDisplayCreateImage(displayID, rect: rect) else { return }

        if fallbackPixelBuffer == nil || fallbackContext == nil {
            prepareFallbackBuffers()
        }

        guard let buffer = fallbackPixelBuffer else { return }

        CVPixelBufferLockBaseAddress(buffer, [])
        guard let baseAddress = CVPixelBufferGetBaseAddress(buffer) else {
            CVPixelBufferUnlockBaseAddress(buffer, [])
            return
        }

        if fallbackContext == nil || fallbackBaseAddress != baseAddress {
            fallbackContext = makeFallbackContext(buffer: buffer, baseAddress: baseAddress)
            fallbackBaseAddress = baseAddress
        }

        guard let ctx = fallbackContext else {
            CVPixelBufferUnlockBaseAddress(buffer, [])
            return
        }

        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        CVPixelBufferUnlockBaseAddress(buffer, [])

        let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
        onFrame?(buffer, ts)
    }

    private func prepareFallbackBuffers() {
        var pb: CVPixelBuffer?
        let status = CVPixelBufferCreate(kCFAllocatorDefault, width, height,
                                          kCVPixelFormatType_32BGRA,
                                          [kCVPixelBufferCGImageCompatibilityKey: true,
                                           kCVPixelBufferCGBitmapContextCompatibilityKey: true,
                                           kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary] as CFDictionary,
                                          &pb)
        guard status == kCVReturnSuccess, let buffer = pb else { return }

        fallbackPixelBuffer = buffer
        fallbackContext = nil
        fallbackBaseAddress = nil
    }

    private func makeFallbackContext(buffer: CVPixelBuffer, baseAddress: UnsafeMutableRawPointer) -> CGContext? {
        CGContext(data: baseAddress,
                  width: width, height: height,
                  bitsPerComponent: 8,
                  bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
                  space: fallbackColorSpace,
                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
    }

    // ScreenCaptureKit output handler
    class StreamOutput: NSObject, SCStreamOutput {
        weak var capture: ScreenCapture?
        private var loggedFirstFrame = false

        init(capture: ScreenCapture) {
            self.capture = capture
        }

        func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
            guard outputType == .screen else { return }
            guard let pixelBuffer = sampleBuffer.imageBuffer else {
                print("\(ServerText.captureTag) imageBuffer is nil")
                return
            }

            let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
            if !loggedFirstFrame {
                print("\(ServerText.captureTag) received first frame \(ts)")
                loggedFirstFrame = true
            }
            capture?.onFrame?(pixelBuffer, ts)
        }
    }
}
