import CoreGraphics
import Foundation
import VirtualDisplayBridge

struct VirtualDisplayConfiguration {
    let enabled: Bool
    let width: Int
    let height: Int
    let logicalWidth: Int
    let logicalHeight: Int
    let refreshRate: Int
    let pixelsPerInch: Int
    let hiDPI: Bool
    let backingScale: Int
    let name: String

    /// Mirror the Mac's own screen, or make an extra Mac display for the PC?
    /// MACSTREAM_MIRROR_MAIN=1 or MACSTREAM_VIRTUAL_DISPLAY=0: always mirror.
    /// MACSTREAM_VIRTUAL_DISPLAY=1: always an extra display. Otherwise the viewer's
    /// Settings choice decides, and a viewer that doesn't say gets the mirror.
    static func wantsExtraDisplay(clientInfo: ClientDisplayInfo?) -> Bool {
        if environmentValue("MACSTREAM_MIRROR_MAIN") == "1" { return false }
        switch environmentValue("MACSTREAM_VIRTUAL_DISPLAY") {
        case "0": return false
        case "1": return true
        default: return clientInfo?.screen?.extraDisplay ?? false
        }
    }

    static func load(clientInfo: ClientDisplayInfo?, fps: Int) -> VirtualDisplayConfiguration {
        let enabled = wantsExtraDisplay(clientInfo: clientInfo)
        let screen = clientInfo?.screen

        // A size picked in the viewer's Settings, else the PC monitor's size.
        let pickedSize = screen.map { validDimension($0.width) && validDimension($0.height) } ?? false
        let clientWidth = pickedSize ? screen!.width
            : (validDimension(clientInfo?.width) ? clientInfo!.width : 1920)
        let clientHeight = pickedSize ? screen!.height
            : (validDimension(clientInfo?.height) ? clientInfo!.height : 1080)
        let clientRefresh = validRefreshRate(clientInfo?.refreshRate) ? clientInfo!.refreshRate : fps

        let width = envInt("MACSTREAM_VIRTUAL_WIDTH", min: 640, max: 16_384) ?? clientWidth
        let height = envInt("MACSTREAM_VIRTUAL_HEIGHT", min: 360, max: 16_384) ?? clientHeight
        let refreshRate = envInt("MACSTREAM_VIRTUAL_REFRESH", min: 24, max: 120) ?? clientRefresh
        let backingScale = virtualBackingScale(width: width, height: height, dpi: clientInfo?.dpi ?? 0,
                                               textScale: screen?.textScale ?? 0)
        let hiDPI = backingScale > 1
        let logicalWidth = envInt("MACSTREAM_LOGICAL_WIDTH", min: 320, max: 16_384) ?? max(320, width / backingScale)
        let logicalHeight = envInt("MACSTREAM_LOGICAL_HEIGHT", min: 180, max: 16_384) ?? max(180, height / backingScale)
        let ppi = envInt("MACSTREAM_VIRTUAL_PPI", min: 50, max: 500) ?? (hiDPI ? 220 : 110)
        let name = environmentValue("MACSTREAM_DISPLAY_NAME") ?? "Macstream"

        return VirtualDisplayConfiguration(
            enabled: enabled,
            width: alignVideoDimension(width),
            height: alignVideoDimension(height),
            logicalWidth: logicalWidth,
            logicalHeight: logicalHeight,
            refreshRate: refreshRate,
            pixelsPerInch: ppi,
            hiDPI: hiDPI,
            backingScale: backingScale,
            name: name)
    }

    private static func virtualBackingScale(width: Int, height: Int, dpi: Int, textScale: Int) -> Int {
        if let value = environmentValue("MACSTREAM_RETINA"),
           let parsed = Boolish(value) {
            return parsed ? 2 : 1
        }

        if let value = environmentValue("MACSTREAM_HIDPI"),
           let parsed = Boolish(value) {
            return parsed ? 2 : 1
        }

        if let scale = envInt("MACSTREAM_BACKING_SCALE", min: 1, max: 3) {
            return scale
        }

        // The viewer's Settings: 1 = normal (one Mac pixel per PC pixel), 2 = Retina.
        if textScale == 1 || textScale == 2 {
            return textScale
        }

        if dpi >= 144 {
            return 2
        }

        if dpi > 0 {
            return 1
        }

        return width >= 2560 || height >= 1440 ? 2 : 1
    }

    private static func envInt(_ key: String, min: Int, max: Int) -> Int? {
        guard let value = environmentValue(key),
              let parsed = Int(value),
              parsed >= min,
              parsed <= max else {
            return nil
        }
        return parsed
    }

    private static func validDimension(_ value: Int?) -> Bool {
        guard let value else { return false }
        return value >= 640 && value <= 16_384
    }

    private static func validRefreshRate(_ value: Int?) -> Bool {
        guard let value else { return false }
        return value >= 24 && value <= 120
    }

    private static func Boolish(_ value: String) -> Bool? {
        switch value.lowercased() {
        case "1", "true", "yes", "on":
            return true
        case "0", "false", "no", "off":
            return false
        default:
            return nil
        }
    }
}

final class VirtualDisplay {
    private let handle: WMVirtualDisplayHandle

    var displayID: CGDirectDisplayID {
        handle.displayID
    }

    private init(handle: WMVirtualDisplayHandle) {
        self.handle = handle
    }

    deinit {
        // The CGVirtualDisplay goes with the handle; macOS removes the display shortly after.
        let id = handle.displayID
        print("\(ServerText.virtualDisplayTag) released displayID=\(id)")
        DispatchQueue.global().asyncAfter(deadline: .now() + 1.5) {
            print(VirtualDisplay.isOnline(displayID: id)
                ? "\(ServerText.virtualDisplayTag) displayID=\(id) is still online 1.5 s after release"
                : "\(ServerText.virtualDisplayTag) displayID=\(id) removed from macOS")
        }
    }

    static func create(configuration: VirtualDisplayConfiguration) -> VirtualDisplay? {
        guard WMVirtualDisplayIsAvailable() else {
            print("\(ServerText.virtualDisplayTag) CGVirtualDisplay runtime classes are not available on this macOS version")
            return nil
        }

        var options = WMVirtualDisplayOptions()
        options.width = UInt32(configuration.width)
        options.height = UInt32(configuration.height)
        options.logicalWidth = UInt32(configuration.logicalWidth)
        options.logicalHeight = UInt32(configuration.logicalHeight)
        options.refreshRate = Double(configuration.refreshRate)
        options.pixelsPerInch = UInt32(configuration.pixelsPerInch)
        options.hiDPI = configuration.hiDPI

        return configuration.name.withCString { namePtr in
            options.name = namePtr
            var error: NSError?
            guard let handle = WMVirtualDisplayMake(options, &error) else {
                if let error {
                    print("\(ServerText.virtualDisplayTag) creation failed: \(error.localizedDescription)")
                } else {
                    print("\(ServerText.virtualDisplayTag) creation failed: unknown error")
                }
                return nil
            }

            let display = VirtualDisplay(handle: handle)
            guard waitUntilOnline(displayID: display.displayID) else {
                print("\(ServerText.virtualDisplayTag) displayID=\(display.displayID) did not appear in the online display list")
                DisplayDiagnostics.printOnlineDisplays(prefix: ServerText.virtualDisplayTag)
                return nil
            }
            print("\(ServerText.virtualDisplayTag) created \(configuration.width)x\(configuration.height) backing pixels, logical \(configuration.logicalWidth)x\(configuration.logicalHeight) @ \(configuration.refreshRate)Hz, displayID=\(display.displayID), HiDPI=\(configuration.hiDPI), scale=\(configuration.backingScale)x")
            DisplayDiagnostics.printOnlineDisplays(prefix: ServerText.virtualDisplayTag)
            return display
        }
    }

    private static func waitUntilOnline(displayID: CGDirectDisplayID) -> Bool {
        let deadline = Date().addingTimeInterval(2)
        while Date() < deadline {
            if isOnline(displayID: displayID) {
                return true
            }
            Thread.sleep(forTimeInterval: 0.05)
        }
        return false
    }

    fileprivate static func isOnline(displayID: CGDirectDisplayID) -> Bool {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else {
            return false
        }

        var displays = Array(repeating: CGDirectDisplayID(), count: Int(count))
        guard CGGetOnlineDisplayList(count, &displays, &count) == .success else {
            return false
        }

        return displays.prefix(Int(count)).contains(displayID)
    }
}
