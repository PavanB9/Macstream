import Foundation

/// Per-second streaming counters, sent to the Windows client as a STATS packet so it can show
/// what the Mac side is doing (capture rate, encode rate, encode latency, skips, bitrate).
/// Thread-safe: capture, encoder callback, encoder queue and timer threads all update it.
final class StreamStats {
    private let lock = NSLock()
    private var captured: UInt32 = 0
    private var encoded: UInt32 = 0
    private var encodedBytes: UInt64 = 0
    private var skipped: UInt32 = 0
    private var latencySumMs: Double = 0
    private var latencyMaxMs: Double = 0
    private var encoderSumMs: Double = 0
    private var lastSnapshot = Date()

    func addCaptured() {
        lock.lock(); captured &+= 1; lock.unlock()
    }

    func addSkipped() {
        lock.lock(); skipped &+= 1; lock.unlock()
    }

    func addEncoded(bytes: Int, latencyMs: Double, encoderMs: Double) {
        lock.lock()
        encoded &+= 1
        encoderSumMs += encoderMs
        encodedBytes &+= UInt64(bytes)
        latencySumMs += latencyMs
        latencyMaxMs = max(latencyMaxMs, latencyMs)
        lock.unlock()
    }

    /// Builds the STATS payload (kind 1, version 2) and resets the counters.
    ///
    /// Layout, little endian (49 bytes):
    /// u8 kind=1, u8 version=2, u32 width, u32 height, u8 codec (1=H.264, 2=HEVC),
    /// f32 captureFps, f32 encodeFps, f32 encodeMsAvg, f32 encodeMsMax (capture -> encoded),
    /// u32 bitrateKbps, u32 targetBitrateKbps, u32 skippedForBackpressure, u32 droppedFrames, u8 maxQP,
    /// [v2] f32 encoderMsAvg (time inside VideoToolbox only), u8 flags (bit0 low-latency encoder,
    /// bit1 YUV capture, bit2 streaming an extra Mac display rather than the Mac's own screen)
    func takePayload(width: Int, height: Int, isHEVC: Bool, targetBitRate: Int, droppedFrames: UInt64, maxQP: Int,
                     lowLatency: Bool, yuvCapture: Bool, extraDisplay: Bool = false) -> Data {
        lock.lock()
        let now = Date()
        let elapsed = max(now.timeIntervalSince(lastSnapshot), 0.001)
        let captureFps = Double(captured) / elapsed
        let encodeFps = Double(encoded) / elapsed
        let avgMs = encoded > 0 ? latencySumMs / Double(encoded) : 0
        let maxMs = latencyMaxMs
        let encoderMs = encoded > 0 ? encoderSumMs / Double(encoded) : 0
        let kbps = Double(encodedBytes) * 8 / 1000 / elapsed
        let skippedCount = skipped
        captured = 0; encoded = 0; encodedBytes = 0; skipped = 0
        latencySumMs = 0; latencyMaxMs = 0; encoderSumMs = 0; lastSnapshot = now
        lock.unlock()

        var data = Data(capacity: 49)
        data.appendLE(UInt8(1))
        data.appendLE(UInt8(2))
        data.appendLE(UInt32(clamping: width))
        data.appendLE(UInt32(clamping: height))
        data.appendLE(UInt8(isHEVC ? 2 : 1))
        data.appendLE(Float(captureFps))
        data.appendLE(Float(encodeFps))
        data.appendLE(Float(avgMs))
        data.appendLE(Float(maxMs))
        data.appendLE(UInt32(clamping: Int(kbps)))
        data.appendLE(UInt32(clamping: targetBitRate / 1000))
        data.appendLE(skippedCount)
        data.appendLE(UInt32(clamping: droppedFrames))
        data.appendLE(UInt8(clamping: maxQP))
        data.appendLE(Float(encoderMs))
        data.appendLE(UInt8((lowLatency ? 1 : 0) | (yuvCapture ? 2 : 0) | (extraDisplay ? 4 : 0)))
        return data
    }
}

extension Data {
    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        var le = value.littleEndian
        Swift.withUnsafeBytes(of: &le) { append(contentsOf: $0) }
    }

    mutating func appendLE(_ value: Float) {
        appendLE(value.bitPattern)
    }
}
