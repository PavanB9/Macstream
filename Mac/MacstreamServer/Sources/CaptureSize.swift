import CoreGraphics
import Foundation

/// Decides the pixel size the server captures and encodes for a display.
///
/// A Retina Mac renders a scaled "looks like" mode into a backing store that is usually
/// larger than the physical panel (13" M2 Air at the default "looks like 1470x956":
/// 2940x1912 backing, 2560x1664 panel) and then downsamples it to the panel.
///
/// Default (`MACSTREAM_CAPTURE_SIZE` unset or `panel`): for the built-in display, capture
/// at the panel's native pixels, exactly what the screen shows, with fewer pixels to encode.
/// `backing`: capture the current mode's full backing store (the upstream behaviour).
/// External and virtual displays always use the current mode's backing size.
enum CaptureSize {
    struct Result {
        let width: Int
        let height: Int
        let description: String
    }

    /// kDisplayModeNativeFlag from IOKit's IOGraphicsTypes.h.
    private static let nativeModeFlag: UInt32 = 0x0200_0000

    static func pixels(for displayID: CGDirectDisplayID, scale: Double = 1) -> Result? {
        let backing = backingPixels(for: displayID)
        let mode = environmentValue("MACSTREAM_CAPTURE_SIZE")?.lowercased() ?? "panel"

        var chosen = backing
        var source = "current mode backing store"
        if mode != "backing", CGDisplayIsBuiltin(displayID) != 0, let panel = panelPixels(for: displayID) {
            // Sanity check: a real panel is never far smaller than the backing store (2560 vs 2940
            // at default scaling, 2560 vs 3420 at "More Space"). If detection picked some small
            // 1x mode instead, keep the backing size rather than streaming a blurry picture.
            if let backing, Double(panel.width) < Double(backing.width) * 0.6 {
                print("[Capture] panel size \(panel.width)x\(panel.height) looks wrong next to backing \(backing.width)x\(backing.height); using backing")
            } else {
                chosen = panel
                source = "built-in panel native pixels"
            }
        }

        guard let base = chosen else { return nil }
        let width = alignVideoDimension(Int(Double(base.width) * scale))
        let height = alignVideoDimension(Int(Double(base.height) * scale))
        var description = "\(width)x\(height) (\(source)"
        if let backing, backing.width != width || backing.height != height {
            description += "; backing store is \(backing.width)x\(backing.height)"
        }
        description += ")"
        return Result(width: width, height: height, description: description)
    }

    private static func backingPixels(for displayID: CGDirectDisplayID) -> (width: Int, height: Int)? {
        if let mode = CGDisplayCopyDisplayMode(displayID), mode.pixelWidth > 0, mode.pixelHeight > 0 {
            return (mode.pixelWidth, mode.pixelHeight)
        }
        let width = CGDisplayPixelsWide(displayID)
        let height = CGDisplayPixelsHigh(displayID)
        return width > 0 && height > 0 ? (width, height) : nil
    }

    /// The panel's physical resolution: the native-flagged 1x mode, otherwise the largest 1x mode
    /// (on a Retina panel the largest unscaled mode is the panel itself).
    private static func panelPixels(for displayID: CGDirectDisplayID) -> (width: Int, height: Int)? {
        let options: [CFString: Any] = [kCGDisplayShowDuplicateLowResolutionModes: true]
        guard let modes = CGDisplayCopyAllDisplayModes(displayID, options as CFDictionary) as? [CGDisplayMode], !modes.isEmpty else {
            return nil
        }

        let unscaled = modes.filter { $0.pixelWidth == $0.width && $0.pixelHeight == $0.height }
        let area: (CGDisplayMode) -> Int = { $0.pixelWidth * $0.pixelHeight }

        if let native = unscaled.filter({ $0.ioFlags & nativeModeFlag != 0 }).max(by: { area($0) < area($1) }) {
            return (native.pixelWidth, native.pixelHeight)
        }
        if let largest = unscaled.max(by: { area($0) < area($1) }) {
            return (largest.pixelWidth, largest.pixelHeight)
        }
        return nil
    }
}
