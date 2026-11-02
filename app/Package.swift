// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "ChatSpace",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "ChatSpace",
            path: "Sources/ChatSpace"
        )
    ]
)
