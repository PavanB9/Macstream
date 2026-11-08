import Foundation

/// Log tags and the start-up banner of the Mac server.
enum ServerText {
    static let mainTag = "[Main]"
    static let serverTag = "[Server]"
    static let captureTag = "[Capture]"
    static let encoderTag = "[Encoder]"
    static let virtualDisplayTag = "[VirtualDisplay]"
    static let statsTag = "[Stats]"
    static let rawStatsTag = "[Stats-RAW]"
    static let captureStatsTag = "[CaptureStats]"
    static let rawCaptureStatsTag = "[CaptureStats-RAW]"

    static let bannerLines = [
        "╔══════════════════════════════════════════╗",
        "║   Macstream Server                       ║",
        "║   Mac screen and input over a wired link ║",
        "╚══════════════════════════════════════════╝",
    ]
}
