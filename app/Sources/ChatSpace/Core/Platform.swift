import Foundation

enum Platform: String, CaseIterable, Identifiable, Sendable {
    case zalo, messenger, whatsapp, telegram

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .zalo: "Zalo"
        case .messenger: "Messenger"
        case .whatsapp: "WhatsApp"
        case .telegram: "Telegram"
        }
    }

    var dataDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("ChatSpace", isDirectory: true)
            .appendingPathComponent(rawValue, isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    /// Executable + arguments for this platform's sidecar.
    ///
    /// Packaged app: binaries live in `Contents/Resources/bridges/`.
    /// Development: `CHATSPACE_ROOT` (or the repo found by walking up from the executable)
    /// is used, running the TypeScript bridge through Bun.
    func bridgeCommand() throws -> (URL, [String]) {
        let dataArgs = ["--data-dir", dataDirectory.path]
        let fm = FileManager.default

        if let res = Bundle.main.resourceURL?.appendingPathComponent("bridges") {
            let node = res.appendingPathComponent("chatspace-bridge")
            let meta = res.appendingPathComponent("chatspace-messenger")
            if self == .messenger, fm.isExecutableFile(atPath: meta.path) {
                return (meta, dataArgs)
            }
            if self != .messenger, fm.isExecutableFile(atPath: node.path) {
                return (node, ["--platform", rawValue] + dataArgs)
            }
        }

        guard let root = Platform.repoRoot() else {
            throw BridgeError.launch("Không tìm thấy thư mục bridges. Đặt biến môi trường CHATSPACE_ROOT.")
        }
        if self == .messenger {
            let bin = root.appendingPathComponent("bridges/messenger/chatspace-messenger")
            guard fm.isExecutableFile(atPath: bin.path) else {
                throw BridgeError.launch("Chưa build Messenger bridge: chạy `go build -o chatspace-messenger .` trong bridges/messenger")
            }
            return (bin, dataArgs)
        }
        let compiled = root.appendingPathComponent("bridges/node/dist/chatspace-bridge")
        if fm.isExecutableFile(atPath: compiled.path), ProcessInfo.processInfo.environment["CHATSPACE_DEV"] == nil {
            return (compiled, ["--platform", rawValue] + dataArgs)
        }
        guard let bun = Platform.findBun() else {
            throw BridgeError.launch("Không tìm thấy Bun. Cài tại https://bun.sh hoặc build bridge bằng `bun run build`.")
        }
        let main = root.appendingPathComponent("bridges/node/src/main.ts")
        return (bun, [main.path, "--platform", rawValue] + dataArgs)
    }

    static func repoRoot() -> URL? {
        let fm = FileManager.default
        if let env = ProcessInfo.processInfo.environment["CHATSPACE_ROOT"] {
            return URL(fileURLWithPath: env)
        }
        var dir = Bundle.main.executableURL?.deletingLastPathComponent()
        while let d = dir, d.path != "/" {
            if fm.fileExists(atPath: d.appendingPathComponent("bridges/node/src/main.ts").path) { return d }
            dir = d.deletingLastPathComponent()
        }
        return nil
    }

    static func findBun() -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let candidates = ["\(home)/.bun/bin/bun", "/opt/homebrew/bin/bun", "/usr/local/bin/bun"]
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }.map(URL.init(fileURLWithPath:))
    }
}
