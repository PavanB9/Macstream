// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MacstreamServer",
    platforms: [.macOS(.v13)],
    targets: [
        .target(
            name: "VirtualDisplayBridge",
            path: "Bridge/VirtualDisplayBridge",
            publicHeadersPath: "include",
            // The bridge relies on ARC to free the virtual display (SwiftPM normally turns
            // it on already; this makes sure).
            cSettings: [.unsafeFlags(["-fobjc-arc"])],
            linkerSettings: [
                .linkedFramework("CoreGraphics"),
                .linkedFramework("Foundation"),
            ]
        ),
        .executableTarget(
            name: "MacstreamServer",
            dependencies: ["VirtualDisplayBridge"],
            path: "Sources"
        )
    ]
)
