import Foundation
import CoreGraphics
import CoreVideo
import CoreMedia
import VideoToolbox

enum EncodedVideoCodec: Equatable {
    case h264
    case hevc

    var name: String {
        switch self {
        case .h264:
            return "H.264"
        case .hevc:
            return "HEVC"
        }
    }

    var codecType: CMVideoCodecType {
        switch self {
        case .h264:
            return kCMVideoCodecType_H264
        case .hevc:
            return kCMVideoCodecType_HEVC
        }
    }

    var packetType: PacketType {
        switch self {
        case .h264:
            return .frameH264
        case .hevc:
            return .frameHevc
        }
    }

    static func load() -> EncodedVideoCodec {
        let value = environmentValue("MACSTREAM_CODEC")?
            .lowercased()
            .trimmingCharacters(in: .whitespacesAndNewlines)

        switch value {
        case "hevc", "h265", "h.265":
            return .hevc
        default:
            return .h264
        }
    }
}

class H264Encoder {
    private var session: VTCompressionSession?
    private let width: Int32
    private let height: Int32
    private var frameIndex: UInt64 = 0
    private var spsPPS: Data?
    private let fps: Int

    // Cache the pixel buffer and color space instead of rebuilding them every frame
    private var cachedPixelBuffer: CVPixelBuffer?
    private var cachedContext: CGContext?
    private let colorSpace = CGColorSpaceCreateDeviceRGB()
    private var needsKeyFrame = true
    private let keyFrameRequestLock = NSLock()

    /// Makes the next encoded frame an IDR keyframe. Safe to call from any thread
    /// (the network server calls it when a viewer connects or a frame had to be dropped).
    func requestKeyFrame() {
        keyFrameRequestLock.lock()
        needsKeyFrame = true
        keyFrameRequestLock.unlock()
    }
    private var encodeInputCount: UInt64 = 0
    private var encodeOutputCount: UInt64 = 0
    private var lastInputReportTime = Date()
    private var lastReportedInputCount: UInt64 = 0
    private var lastReportedOutputCount: UInt64 = 0
    private let keyFrameInterval: UInt64
    private let bitRate: Int
    private let completeEveryFrame: Bool
    private let completeFrameInterval: UInt64
    private let quality: Double
    private let codec: EncodedVideoCodec
    private let timestampLock = NSLock()
    private var frameTimestamps: [Int64: UInt64] = [:]

    var onNALUnit: ((Data, Bool, UInt64, UInt64) -> Void)?
    var packetType: PacketType { codec.packetType }
    var codecName: String { codec.name }
    var usesDefaultCodec: Bool { codec == .h264 }

    init(width: Int, height: Int, fps: Int = 60) {
        self.width = Int32(width)
        self.height = Int32(height)
        self.fps = fps
        self.codec = EncodedVideoCodec.load()
        if let value = environmentValue("MACSTREAM_KEY_INTERVAL"),
           let parsed = UInt64(value),
           parsed > 0 {
            keyFrameInterval = parsed
        } else {
            keyFrameInterval = UInt64(max(fps * 4, 120))
        }

        if let value = environmentValue("MACSTREAM_BITRATE"),
           let parsed = Int(value),
           parsed > 0 {
            bitRate = parsed
        } else {
            bitRate = Self.defaultBitRate(width: Int32(width), height: Int32(height), fps: fps)
        }

        if environmentValue("MACSTREAM_ASYNC_ENCODER") == "1" {
            completeEveryFrame = false
        } else if environmentValue("MACSTREAM_SYNC_ENCODER") == "1" {
            completeEveryFrame = true
        } else {
            completeEveryFrame = false
        }

        if let value = environmentValue("MACSTREAM_FLUSH_INTERVAL"),
           let parsed = UInt64(value),
           parsed <= 120 {
            completeFrameInterval = parsed
        } else {
            completeFrameInterval = UInt64(max(2, min(4, fps / 20)))
        }

        // VideoToolbox treats Quality = 1.0 as "near-lossless": on Apple Silicon it then emits
        // H.264 "High 4:4:4 Predictive" (profile_idc 244) even when High is requested. Typical
        // GPU decoders (NVIDIA/Intel/AMD via D3D11VA/DXVA2) don't support that profile, so the
        // Windows client got garbage. Keep H.264 below 1.0 so it stays in High profile.
        let requestedQuality: Double
        if let value = environmentValue("MACSTREAM_QUALITY"),
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            requestedQuality = parsed
        } else {
            requestedQuality = 0.9
        }
        quality = codec == .h264 ? min(requestedQuality, 0.95) : requestedQuality
    }

    func start() -> Bool {
        var session: VTCompressionSession?

        // sourceImageBufferAttributes
        let sourceAttributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(width),
            kCVPixelBufferHeightKey as String: Int(height),
            kCVPixelBufferIOSurfacePropertiesKey as String: [:],
        ]

        let outputCallback: VTCompressionOutputCallback = { outputCallbackRefCon, _, status, _, sampleBuffer in
            let enc = Unmanaged<H264Encoder>.fromOpaque(outputCallbackRefCon!).takeUnretainedValue()
            guard status == noErr else {
                print("\(ServerText.encoderTag) output callback failed: \(status)")
                return
            }
            guard let sampleBuffer = sampleBuffer else {
                print("\(ServerText.encoderTag) output callback sampleBuffer is nil")
                return
            }
            enc.handleEncodedFrame(sampleBuffer: sampleBuffer)
        }

        func createSession(lowLatency: Bool) -> OSStatus {
            // Low-latency rate control (Apple's mode for video calls / screen sharing) emits each
            // frame as soon as it is encoded instead of pipelining several frames: in testing the
            // default mode added ~20-30 ms between capture and encoded output on an M2.
            var specification: [CFString: Any] = [
                kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder: true,
            ]
            if lowLatency {
                specification[kVTVideoEncoderSpecification_EnableLowLatencyRateControl] = true
            }
            return VTCompressionSessionCreate(
                allocator: nil,
                width: width,
                height: height,
                codecType: codec.codecType,
                encoderSpecification: specification as CFDictionary,
                imageBufferAttributes: sourceAttributes as CFDictionary,
                compressedDataAllocator: nil,
                outputCallback: outputCallback,
                refcon: Unmanaged.passUnretained(self).toOpaque(),
                compressionSessionOut: &session
            )
        }

        let wantLowLatency = environmentValue("MACSTREAM_LOW_LATENCY") != "0"
        var status = createSession(lowLatency: wantLowLatency)
        lowLatencyMode = wantLowLatency && status == noErr
        if status != noErr && wantLowLatency {
            print("\(ServerText.encoderTag) low-latency encoder unavailable (\(status)); using the standard encoder")
            session = nil
            status = createSession(lowLatency: false)
        }

        guard status == noErr, let session = session else {
            print("\(ServerText.encoderTag) creation failed: \(status)")
            return false
        }

        var err: OSStatus

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        if err != noErr { print("\(ServerText.encoderTag) failed to set RealTime: \(err)") }

        // Measured ~15 ms inside VideoToolbox per 2560x1664 frame even in low-latency mode. Ask the
        // hardware encoder to favour speed; the QP cap below still keeps text crisp.
        // MACSTREAM_SPEED_OVER_QUALITY=0 turns it off.
        if environmentValue("MACSTREAM_SPEED_OVER_QUALITY") != "0" {
            err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, value: kCFBooleanTrue)
            if err != noErr { print("\(ServerText.encoderTag) failed to set PrioritizeEncodingSpeedOverQuality: \(err)") }
        }

        let profile = Self.profileLevel(codec: codec, lowLatency: lowLatencyMode)
        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: profile.value as AnyObject)
        if err != noErr { print("\(ServerText.encoderTag) failed to set ProfileLevel: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: bitRate))
        if err != noErr { print("\(ServerText.encoderTag) failed to set BitRate: \(err)") }

        // DataRateLimits: [bytes, seconds]
        let dataRateLimits = [
            NSNumber(value: (bitRate * 3) / 8),
            NSNumber(value: 1),
        ] as CFArray
        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_DataRateLimits, value: dataRateLimits)
        if err != noErr { print("\(ServerText.encoderTag) failed to set DataRateLimits: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxFrameDelayCount, value: NSNumber(value: 0))
        if err != noErr { print("\(ServerText.encoderTag) failed to set MaxFrameDelay: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        if err != noErr { print("\(ServerText.encoderTag) failed to set FrameReordering: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: fps))
        if err != noErr { print("\(ServerText.encoderTag) failed to set ExpectedFrameRate: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: keyFrameInterval))
        if err != noErr { print("\(ServerText.encoderTag) failed to set MaxKeyFrameInterval: \(err)") }

        let keyFrameIntervalDuration = Double(keyFrameInterval) / Double(max(fps, 1))
        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: NSNumber(value: keyFrameIntervalDuration))
        if err != noErr { print("\(ServerText.encoderTag) failed to set MaxKeyFrameIntervalDuration: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_Quality, value: NSNumber(value: quality))
        if err != noErr { print("\(ServerText.encoderTag) failed to set Quality: \(err)") }

        // Cap the quantizer so static desktop content (text, UI edges) stays crisp. Without a cap,
        // VideoToolbox happily encodes simple screens at a few Mbps with soft text. Lower = sharper
        // and more bandwidth; the wired link has plenty. MACSTREAM_MAX_QP=0 disables the cap.
        maxFrameQP = Self.configuredMaxFrameQP()
        if maxFrameQP > 0 {
            err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxAllowedFrameQP, value: NSNumber(value: maxFrameQP))
            if err != noErr {
                print("\(ServerText.encoderTag) failed to set MaxAllowedFrameQP=\(maxFrameQP): \(err)")
                maxFrameQP = 0
            }
        }

        err = VTCompressionSessionPrepareToEncodeFrames(session)
        guard err == noErr else {
            print("\(ServerText.encoderTag) prepare failed: \(err)")
            return false
        }

        self.session = session
        print("\(ServerText.encoderTag) \(codec.name) encoder started (\(width)x\(height) @ \(fps)fps), bitrate: \(Double(bitRate) / 1_000_000.0) Mbps, key interval: \(keyFrameInterval), sync=\(completeEveryFrame), flushInterval=\(completeFrameInterval), quality=\(quality), profile=\(profile.name), maxQP=\(maxFrameQP), lowLatency=\(lowLatencyMode)")
        return true
    }

    private static func profileLevel(codec: EncodedVideoCodec, lowLatency: Bool) -> (name: String, value: CFString) {
        switch codec {
        case .h264:
            let profile = environmentValue("MACSTREAM_H264_PROFILE")?.lowercased()
            switch profile {
            case "baseline":
                return ("baseline", kVTProfileLevel_H264_Baseline_AutoLevel)
            case "main":
                return ("main", kVTProfileLevel_H264_Main_AutoLevel)
            default:
                // Low-latency mode supports Constrained Baseline / Constrained High. Constrained High
                // is High without B-frames (profile_idc 100), which every GPU decoder handles.
                return lowLatency
                    ? ("constrained-high", kVTProfileLevel_H264_ConstrainedHigh_AutoLevel)
                    : ("high", kVTProfileLevel_H264_High_AutoLevel)
            }
        case .hevc:
            let profile = environmentValue("MACSTREAM_HEVC_PROFILE")?.lowercased()
            switch profile {
            case "main10", "10":
                return ("main10", kVTProfileLevel_HEVC_Main10_AutoLevel)
            case "main42210", "42210", "4:2:2":
                return ("main42210", "HEVC_Main42210_AutoLevel" as CFString)
            case "main444", "444", "4:4:4":
                return ("main444-experimental", "HEVC_Main444_AutoLevel" as CFString)
            default:
                return ("main", kVTProfileLevel_HEVC_Main_AutoLevel)
            }
        }
    }

    private(set) var maxFrameQP = 0
    private(set) var lowLatencyMode = false
    var targetBitRate: Int { bitRate }

    private static func configuredMaxFrameQP() -> Int {
        if let value = environmentValue("MACSTREAM_MAX_QP"), let parsed = Int(value), (0...51).contains(parsed) {
            return parsed
        }
        return 24
    }

    private static func defaultBitRate(width: Int32, height: Int32, fps: Int) -> Int {
        let bitsPerPixelFrame: Double
        if let value = environmentValue("MACSTREAM_BITRATE_BPP"),
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            bitsPerPixelFrame = parsed
        } else {
            bitsPerPixelFrame = 0.5   // ~128 Mbps at 2560x1664@60; the wired link carries far more
        }

        let pixelRate = Double(width) * Double(height) * Double(max(fps, 1))
        let calculated = Int(pixelRate * bitsPerPixelFrame)
        return min(max(calculated, 60_000_000), 300_000_000)
    }

    func encode(pixelBuffer: CVPixelBuffer, timestamp: UInt64) {
        guard let session = session else { return }

        encodeInputCount += 1
        let now = Date()
        if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
            let elapsed = now.timeIntervalSince(lastInputReportTime)
            let inputFps = Double(encodeInputCount - lastReportedInputCount) / elapsed
            let outputFps = Double(encodeOutputCount - lastReportedOutputCount) / elapsed
            print("\(ServerText.encoderTag) input FPS: \(String(format: "%.1f", inputFps)), output FPS: \(String(format: "%.1f", outputFps)), totalIn=\(encodeInputCount), totalOut=\(encodeOutputCount)")
            lastInputReportTime = now
            lastReportedInputCount = encodeInputCount
            lastReportedOutputCount = encodeOutputCount
        }

        let presentationTime = CMTime(value: Int64(frameIndex), timescale: CMTimeScale(fps))
        frameIndex += 1
        timestampLock.lock()
        frameTimestamps[presentationTime.value] = timestamp
        submitTimes[presentationTime.value] = CFAbsoluteTimeGetCurrent()
        timestampLock.unlock()

        keyFrameRequestLock.lock()
        let forcedKeyFrame = needsKeyFrame
        needsKeyFrame = false
        keyFrameRequestLock.unlock()
        let isKeyFrame = forcedKeyFrame || frameIndex % keyFrameInterval == 0
        let frameProps: CFDictionary? = isKeyFrame
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
            : nil

        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentationTime,
            duration: .invalid,
            frameProperties: frameProps,
            sourceFrameRefcon: nil,
            infoFlagsOut: nil
        )

        if status != noErr {
            print("\(ServerText.encoderTag) EncodeFrame failed: \(status)")
            timestampLock.lock()
            frameTimestamps.removeValue(forKey: presentationTime.value)
            submitTimes.removeValue(forKey: presentationTime.value)
            timestampLock.unlock()
        }

        // Don't wait for the hardware encoder synchronously on every frame by default: at 5K or high
        // bitrates CompleteFrames then caps the stream at ~30 FPS. Keyframes and the first frames are
        // still flushed at once; the periodic flush bounds the latency of the asynchronous encoder.
        if completeEveryFrame ||
            encodeInputCount <= 3 ||
            isKeyFrame ||
            (!lowLatencyMode && completeFrameInterval > 0 && frameIndex % completeFrameInterval == 0) {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: presentationTime)
        }
    }

    func stop() {
        if let session = session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        }
        session = nil
        timestampLock.lock()
        frameTimestamps.removeAll()
        submitTimes.removeAll()
        timestampLock.unlock()
        print("\(ServerText.encoderTag) stopped")
    }

    private func handleEncodedFrame(sampleBuffer: CMSampleBuffer) {
        guard sampleBuffer.numSamples > 0 else {
            print("\(ServerText.encoderTag) sampleBuffer has no samples")
            return
        }

        var isKeyFrame = isSampleKeyFrame(sampleBuffer)
        if encodeOutputCount == 0 {
            isKeyFrame = true
        }

        if isKeyFrame {
            spsPPS = extractParameterSets(from: sampleBuffer)
            if spsPPS == nil {
                print("\(ServerText.encoderTag) failed to get parameter sets from key frame")
            } else if !loggedStreamProfile, codec == .h264, let spsPPS {
                loggedStreamProfile = true
                logH264Profile(parameterSets: spsPPS)
            }
        }

        guard let sliceData = extractSliceData(from: sampleBuffer) else {
            print("\(ServerText.encoderTag) failed to get slice data")
            return
        }

        var nalData = Data()
        if isKeyFrame, let spsPPS = spsPPS {
            nalData.append(spsPPS)
        }
        nalData.append(sliceData)

        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestamp = takeFrameTimestamp(presentationTime: presentationTime)
        encodeOutputCount += 1
        if encodeOutputCount <= 3 || isKeyFrame {
            print("\(ServerText.encoderTag) output frame #\(encodeOutputCount), key=\(isKeyFrame), bytes=\(nalData.count)")
        }
        onNALUnit?(nalData, isKeyFrame, frameIndex, timestamp)
    }

    private var loggedStreamProfile = false

    /// Logs the profile/level the encoder actually produced (from the SPS), since VideoToolbox
    /// may not honour the requested profile. Annex-B: 00 00 00 01, NAL header 0x67, then
    /// profile_idc, constraint flags, level_idc.
    private func logH264Profile(parameterSets: Data) {
        let bytes = [UInt8](parameterSets)
        guard bytes.count >= 8, bytes[4] & 0x1F == 7 else { return }
        let profileIdc = bytes[5]
        let levelIdc = bytes[7]
        let name: String
        switch profileIdc {
        case 66: name = "Baseline"
        case 77: name = "Main"
        case 100: name = "High"
        case 110: name = "High 10"
        case 122: name = "High 4:2:2"
        case 244: name = "High 4:4:4 Predictive"
        default: name = "profile_idc \(profileIdc)"
        }
        print("\(ServerText.encoderTag) H.264 stream profile: \(name), level \(Double(levelIdc) / 10)")
        if profileIdc != 66 && profileIdc != 77 && profileIdc != 100 {
            print("\(ServerText.encoderTag) WARNING: most Windows GPU decoders only support Baseline/Main/High; lower MACSTREAM_QUALITY")
        }
    }

    /// Time spent inside VideoToolbox for the frame most recently delivered to onNALUnit, in ms
    /// (from VTCompressionSessionEncodeFrame to the output callback). Read inside onNALUnit.
    private(set) var lastEncodeDurationMs: Double = 0
    private var submitTimes: [Int64: CFTimeInterval] = [:]

    private func takeFrameTimestamp(presentationTime: CMTime) -> UInt64 {
        timestampLock.lock()
        let timestamp = frameTimestamps.removeValue(forKey: presentationTime.value)
        if let submitted = submitTimes.removeValue(forKey: presentationTime.value) {
            lastEncodeDurationMs = (CFAbsoluteTimeGetCurrent() - submitted) * 1000
        }
        timestampLock.unlock()

        return timestamp ?? UInt64(CFAbsoluteTimeGetCurrent() * 1000)
    }

    private func isSampleKeyFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as NSArray?,
              attachments.count > 0,
              let dict = attachments[0] as? NSDictionary else {
            // No attachments: Core Media treats the sample as a sync (key) frame.
            return true
        }

        if let notSync = dict[kCMSampleAttachmentKey_NotSync] as? Bool {
            return !notSync
        }
        if let dependsOnOthers = dict[kCMSampleAttachmentKey_DependsOnOthers] as? Bool {
            return !dependsOnOthers
        }

        // kCMSampleAttachmentKey_NotSync absent means the sample IS a sync frame.
        return true
    }

    private func extractParameterSets(from sampleBuffer: CMSampleBuffer) -> Data? {
        switch codec {
        case .h264:
            return extractH264ParameterSets(from: sampleBuffer)
        case .hevc:
            return extractHEVCParameterSets(from: sampleBuffer)
        }
    }

    private func extractH264ParameterSets(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer) else { return nil }

        var result = Data()
        var paramCount: Int = 0

        // Get the parameter set count (query index 0 first to get the count)
        let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            description,
            parameterSetIndex: 0,
            parameterSetPointerOut: nil,
            parameterSetSizeOut: nil,
            parameterSetCountOut: &paramCount,
            nalUnitHeaderLengthOut: nil
        )
        guard status == noErr else { return nil }

        for i in 0..<paramCount {
            var ptr: UnsafePointer<UInt8>?
            var size: Int = 0
            guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                description,
                parameterSetIndex: i,
                parameterSetPointerOut: &ptr,
                parameterSetSizeOut: &size,
                parameterSetCountOut: nil,
                nalUnitHeaderLengthOut: nil
            ) == noErr, let ptr = ptr else { continue }

            result.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            result.append(UnsafeBufferPointer(start: ptr, count: size))
        }

        return result.isEmpty ? nil : result
    }

    private func extractHEVCParameterSets(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer) else { return nil }

        var result = Data()
        var paramCount: Int = 0

        let status = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
            description,
            parameterSetIndex: 0,
            parameterSetPointerOut: nil,
            parameterSetSizeOut: nil,
            parameterSetCountOut: &paramCount,
            nalUnitHeaderLengthOut: nil)
        guard status == noErr else { return nil }

        for i in 0..<paramCount {
            var ptr: UnsafePointer<UInt8>?
            var size: Int = 0
            guard CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                description,
                parameterSetIndex: i,
                parameterSetPointerOut: &ptr,
                parameterSetSizeOut: &size,
                parameterSetCountOut: nil,
                nalUnitHeaderLengthOut: nil) == noErr, let ptr = ptr else { continue }

            result.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            result.append(UnsafeBufferPointer(start: ptr, count: size))
        }

        return result.isEmpty ? nil : result
    }

    private func extractSliceData(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return nil }

        let length = CMBlockBufferGetDataLength(blockBuffer)
        var data = Data(count: length)

        data.withUnsafeMutableBytes { ptr in
            guard let base = ptr.baseAddress else { return }
            CMBlockBufferCopyDataBytes(blockBuffer, atOffset: 0, dataLength: length, destination: base)
        }

        // AVCC → Annex-B
        var result = Data()
        var offset = 0
        while offset + 4 <= data.count {
            let b0 = Int(data[offset])
            let b1 = Int(data[offset + 1])
            let b2 = Int(data[offset + 2])
            let b3 = Int(data[offset + 3])
            let nalLen = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3
            offset += 4
            guard offset + nalLen <= data.count else { break }
            result.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            result.append(data[offset..<(offset + nalLen)])
            offset += nalLen
        }

        return result.isEmpty ? nil : result
    }

    private func createPixelBuffer(from image: CGImage) -> CVPixelBuffer? {
        let pxWidth = image.width
        let pxHeight = image.height

        // Reuse the cached pixel buffer
        if cachedPixelBuffer == nil {
            var pb: CVPixelBuffer?
            let status = CVPixelBufferCreate(
                kCFAllocatorDefault,
                Int(width),
                Int(height),
                kCVPixelFormatType_32BGRA,
                [
                    kCVPixelBufferCGImageCompatibilityKey: true,
                    kCVPixelBufferCGBitmapContextCompatibilityKey: true,
                    kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
                ] as CFDictionary,
                &pb
            )
            guard status == kCVReturnSuccess, let buffer = pb else { return nil }
            cachedPixelBuffer = buffer
        }

        guard let buffer = cachedPixelBuffer else { return nil }

        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }

        guard let context = CGContext(
            data: CVPixelBufferGetBaseAddress(buffer),
            width: Int(width),
            height: Int(height),
            bitsPerComponent: 8,
            bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { return nil }

        context.draw(image, in: CGRect(x: 0, y: 0, width: Int(width), height: Int(height)))
        return buffer
    }
}
