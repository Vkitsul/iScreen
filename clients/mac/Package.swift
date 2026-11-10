// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "iScreen",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "iScreen",
            path: "Sources/iScreen",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("Carbon"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("Metal"),
                .linkedFramework("Network"),
                .linkedFramework("QuartzCore"),
                .linkedFramework("VideoToolbox"),
            ]
        ),
    ]
)
