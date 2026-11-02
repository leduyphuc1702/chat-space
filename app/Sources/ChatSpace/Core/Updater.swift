import AppKit
import CryptoKit
import Foundation
import Observation

/// Self-update from the GitHub releases of the public repo: the latest release's `ChatSpace-macos.zip`
/// is downloaded in the background, checked, unpacked next to the running bundle and swapped in when the
/// user restarts from the rail button, or when ChatSpace quits.
@MainActor
@Observable
final class Updater {
    static let shared = Updater()

    static let repo = "leduyphuc1702/chat-space"
    static let assetName = "ChatSpace-macos.zip"
    private static let interval: TimeInterval = 4 * 3600

    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case downloading(String)
        case ready(String)
        case failed(String)
    }

    private(set) var state: State = .idle
    private(set) var lastCheck: Date?
    var automatic: Bool {
        didSet { UserDefaults.standard.set(automatic, forKey: "updates.automatic") }
    }

    let currentVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    /// Only the installed release build updates itself: not `swift run`, the demo copy or the dev bundle.
    let supported = Bundle.main.bundleIdentifier == "vn.chatspace.app" && Bundle.main.bundleURL.pathExtension == "app"
        && !DemoData.enabled

    private var staged: URL?
    private var timer: Timer?

    private init() {
        automatic = UserDefaults.standard.object(forKey: "updates.automatic") as? Bool ?? true
    }

    var readyVersion: String? {
        if case .ready(let v) = state { return v }
        return nil
    }

    func start() {
        guard supported else { return }
        Self.cleanLeftovers()
        timer = Timer.scheduledTimer(withTimeInterval: Self.interval, repeats: true) { _ in
            Task { @MainActor in if Updater.shared.automatic { await Updater.shared.check() } }
        }
        Task {
            try? await Task.sleep(nanoseconds: 15_000_000_000)
            if automatic { await check() }
        }
    }

    func check() async {
        guard supported else { return }
        switch state {
        case .checking, .downloading, .ready: return
        default: break
        }
        state = .checking
        do {
            let release = try await Self.latestRelease()
            lastCheck = Date()
            let version = release.tag_name.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            guard Self.isNewer(version, than: currentVersion) else { state = .upToDate; return }
            guard let asset = release.assets.first(where: { $0.name == Self.assetName }) else {
                state = .upToDate // release published before its macOS build was attached
                return
            }
            state = .downloading(version)
            staged = try await Self.stage(asset, version: version)
            state = .ready(version)
        } catch {
            lastCheck = Date()
            state = .failed(error.localizedDescription)
        }
    }

    /// Quit, swap the bundle, open the new one.
    func restartToUpdate() {
        guard launchInstaller(relaunch: true) else { return }
        NSApp.terminate(nil)
    }

    /// Called from applicationWillTerminate: a downloaded update is installed on the way out.
    func installOnQuit() {
        _ = launchInstaller(relaunch: false)
    }

    private func launchInstaller(relaunch: Bool) -> Bool {
        guard let staged, FileManager.default.fileExists(atPath: staged.path) else { return false }
        let script = """
        pid=$1; old=$2; new=$3; relaunch=$4
        while kill -0 "$pid" 2>/dev/null; do sleep 0.2; done
        backup="$old.previous"
        rm -rf "$backup"
        if mv "$old" "$backup"; then
          if mv "$new" "$old"; then rm -rf "$backup"; else mv "$backup" "$old"; fi
        fi
        rm -rf "$(dirname "$(dirname "$new")")"
        xattr -dr com.apple.quarantine "$old" 2>/dev/null
        [ "$relaunch" = 1 ] && open "$old"
        exit 0
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script, "chatspace-update", String(ProcessInfo.processInfo.processIdentifier),
                       Bundle.main.bundleURL.path, staged.path, relaunch ? "1" : "0"]
        do {
            try p.run()
            self.staged = nil
            return true
        } catch {
            state = .failed("Không chạy được trình cài đặt: \(error.localizedDescription)")
            return false
        }
    }

    // MARK: Release lookup and staging

    private struct Release: Decodable {
        let tag_name: String
        let assets: [Asset]
    }

    private struct Asset: Decodable {
        let name: String
        let size: Int
        let browser_download_url: URL
        let digest: String?
    }

    private static func latestRelease() async throws -> Release {
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("ChatSpace-Updater", forHTTPHeaderField: "User-Agent")
        req.cachePolicy = .reloadIgnoringLocalCacheData
        let (data, resp) = try await URLSession.shared.data(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if code == 404 { throw UpdateError("Chưa có bản phát hành nào") }
        guard code == 200 else { throw UpdateError("GitHub trả về mã \(code)") }
        return try JSONDecoder().decode(Release.self, from: data)
    }

    /// Download, verify and unpack into `<folder of the running app>/.ChatSpace-update/`, so the final swap
    /// is a rename on the same volume.
    private static func stage(_ asset: Asset, version: String) async throws -> URL {
        let fm = FileManager.default
        let parent = Bundle.main.bundleURL.deletingLastPathComponent()
        guard fm.isWritableFile(atPath: parent.path) else {
            throw UpdateError("Không có quyền ghi vào \(parent.path) — hãy chép ChatSpace.app vào thư mục của bạn hoặc /Applications")
        }
        let dir = parent.appendingPathComponent(".ChatSpace-update", isDirectory: true)
        try? fm.removeItem(at: dir)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)

        var req = URLRequest(url: asset.browser_download_url)
        req.setValue("ChatSpace-Updater", forHTTPHeaderField: "User-Agent")
        let (tmp, resp) = try await URLSession.shared.download(for: req)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError("Tải bản cập nhật thất bại") }
        let zip = dir.appendingPathComponent(assetName)
        try fm.moveItem(at: tmp, to: zip)

        let size = (try? fm.attributesOfItem(atPath: zip.path)[.size] as? Int) ?? -1
        guard size == asset.size else { throw UpdateError("File tải về không đủ dung lượng") }
        if let digest = asset.digest, digest.hasPrefix("sha256:") {
            let hash = try await Task.detached { try sha256(zip) }.value
            guard hash == digest.dropFirst("sha256:".count).lowercased() else { throw UpdateError("Sai mã SHA-256 của bản cập nhật") }
        }

        let out = dir.appendingPathComponent("unpacked", isDirectory: true)
        try await run("/usr/bin/ditto", ["-x", "-k", zip.path, out.path])
        try? fm.removeItem(at: zip)
        let app = out.appendingPathComponent("ChatSpace.app")
        guard let info = Bundle(url: app)?.infoDictionary,
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              info["CFBundleShortVersionString"] as? String == version else {
            throw UpdateError("Gói cập nhật không đúng ChatSpace \(version)")
        }
        try await run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        return app
    }

    nonisolated private static func sha256(_ url: URL) throws -> String {
        let h = FileHandle(forReadingAtPath: url.path)
        guard let h else { throw UpdateError("Không đọc được file tải về") }
        defer { try? h.close() }
        var hasher = SHA256()
        while let chunk = try h.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private static func run(_ tool: String, _ args: [String]) async throws {
        try await withCheckedThrowingContinuation { (c: CheckedContinuation<Void, Error>) in
            let p = Process()
            p.executableURL = URL(fileURLWithPath: tool)
            p.arguments = args
            p.standardOutput = FileHandle.nullDevice
            p.standardError = FileHandle.nullDevice
            p.terminationHandler = { p in
                if p.terminationStatus == 0 { c.resume() }
                else { c.resume(throwing: UpdateError("\(URL(fileURLWithPath: tool).lastPathComponent) lỗi \(p.terminationStatus)")) }
            }
            do { try p.run() } catch { c.resume(throwing: error) }
        }
    }

    /// A swap interrupted by a crash or power loss leaves these behind.
    private static func cleanLeftovers() {
        let parent = Bundle.main.bundleURL.deletingLastPathComponent()
        try? FileManager.default.removeItem(at: parent.appendingPathComponent(".ChatSpace-update"))
        try? FileManager.default.removeItem(at: Bundle.main.bundleURL.appendingPathExtension("previous"))
    }

    nonisolated static func isNewer(_ a: String, than b: String) -> Bool {
        let x = a.split(separator: ".").map { Int($0) ?? 0 }, y = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(x.count, y.count) {
            let l = i < x.count ? x[i] : 0, r = i < y.count ? y[i] : 0
            if l != r { return l > r }
        }
        return false
    }
}

struct UpdateError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
