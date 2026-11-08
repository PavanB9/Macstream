import CoreGraphics
import Foundation
import ScreenCaptureKit

enum DisplayDiagnostics {
    static func printOnlineDisplays(prefix: String) {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success else {
            print("\(prefix) failed to get online display count")
            return
        }

        var displays = Array(repeating: CGDirectDisplayID(), count: Int(count))
        guard CGGetOnlineDisplayList(count, &displays, &count) == .success else {
            print("\(prefix) failed to get online display list")
            return
        }

        print("\(prefix) online displays \(count):")
        for displayID in displays.prefix(Int(count)) {
            let bounds = CGDisplayBounds(displayID)
            let mode = CGDisplayCopyDisplayMode(displayID)
            let modeText: String
            if let mode {
                modeText = "\(mode.width)x\(mode.height) points, \(mode.pixelWidth)x\(mode.pixelHeight) pixels @ \(String(format: "%.1f", mode.refreshRate))Hz"
            } else {
                modeText = "mode=nil"
            }

            let mirror = CGDisplayMirrorsDisplay(displayID)
            let mirrorText = mirror == kCGNullDirectDisplay ? "none" : "\(mirror)"
            print("\(prefix) - id=\(displayID), main=\(CGDisplayIsMain(displayID) != 0), builtin=\(CGDisplayIsBuiltin(displayID) != 0), active=\(CGDisplayIsActive(displayID) != 0), bounds=\(Int(bounds.origin.x)),\(Int(bounds.origin.y)) \(Int(bounds.width))x\(Int(bounds.height)), pixels=\(CGDisplayPixelsWide(displayID))x\(CGDisplayPixelsHigh(displayID)), mirror=\(mirrorText), mode=\(modeText)")
        }
    }

    static func printShareableDisplays(_ displays: [SCDisplay], target displayID: CGDirectDisplayID, prefix: String) {
        print("\(prefix) ScreenCaptureKit displays \(displays.count), target=\(displayID):")
        for display in displays {
            print("\(prefix) - sckID=\(display.displayID), size=\(display.width)x\(display.height), target=\(display.displayID == displayID)")
        }
    }
}
