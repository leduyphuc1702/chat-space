import AppKit
import AVFoundation
import CoreText
import SwiftUI

/// Original artwork, fonts and sounds borrowed at runtime from the official apps installed on
/// this Mac (WhatsApp, Telegram, Zalo). Nothing is copied into ChatSpace's own bundle; when an
/// app is missing every call returns nil and the UI falls back to its drawn look-alike.
@MainActor
final class NativeAssets {
    static let shared = NativeAssets()

    enum Catalog { case whatsapp, whatsappShared, telegram }

    private var bundles: [Catalog: Bundle] = [:]
    private var missing: Set<Catalog> = []
    private var images: [String: NSImage] = [:]
    private var misses: Set<String> = []
    private var asar: AsarArchive??
    private(set) var zaloFontsRegistered = false
    /// Read from font helpers that may run off the main actor; written once at launch.
    nonisolated(unsafe) static var zaloFontsReady = false

    static let bundleIDs: [Platform: [String]] = [
        .whatsapp: ["net.whatsapp.WhatsApp", "desktop.WhatsApp"],
        .telegram: ["ru.keepcoder.Telegram", "org.telegram.desktop"],
        .zalo: ["com.vng.zalo"],
        .messenger: ["com.facebook.archon"],
    ]

    func appURL(_ p: Platform) -> URL? {
        for id in Self.bundleIDs[p] ?? [] {
            if let u = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) { return u }
        }
        return nil
    }

    /// The installed app's own Dock icon.
    func appIcon(_ p: Platform) -> NSImage? {
        let key = "icon/\(p.rawValue)"
        if let i = images[key] { return i }
        guard let url = appURL(p) else { return nil }
        let icon = NSWorkspace.shared.icon(forFile: url.path)
        images[key] = icon
        return icon
    }

    private func bundle(_ c: Catalog) -> Bundle? {
        if let b = bundles[c] { return b }
        if missing.contains(c) { return nil }
        var b: Bundle?
        switch c {
        case .whatsapp: b = appURL(.whatsapp).flatMap { Bundle(url: $0) }
        case .whatsappShared:
            b = appURL(.whatsapp).flatMap { Bundle(url: $0.appendingPathComponent("Contents/Frameworks/SharedModules.framework")) }
        case .telegram: b = appURL(.telegram).flatMap { Bundle(url: $0) }
        }
        if let b { bundles[c] = b } else { missing.insert(c) }
        return b
    }

    /// Named image from an installed app's asset catalog.
    func image(_ name: String, _ c: Catalog) -> NSImage? {
        let key = "\(c)/\(name)"
        if let i = images[key] { return i }
        if misses.contains(key) { return nil }
        guard let img = bundle(c)?.image(forResource: name) else { misses.insert(key); return nil }
        images[key] = img
        return img
    }

    /// Telegram's classic doodle pattern (`builtin-wallpaper-svg`, gzipped SVG with a transparent
    /// background), rasterised once at half its 1440×2960 size for tiling.
    func telegramPattern() -> NSImage? {
        let key = "telegram/pattern"
        if let i = images[key] { return i }
        if misses.contains(key) { return nil }
        guard let url = appURL(.telegram)?.appendingPathComponent("Contents/Resources/builtin-wallpaper-svg"),
              let gz = try? Data(contentsOf: url), gz.count > 18, gz[0] == 0x1F, gz[1] == 0x8B, gz[3] == 0,
              let svg = try? (gz.subdata(in: 10..<gz.count - 8) as NSData).decompressed(using: .zlib) as Data,
              let vector = NSImage(data: svg) else { misses.insert(key); return nil }
        let px = (w: Int(vector.size.width), h: Int(vector.size.height))
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px.w, pixelsHigh: px.h, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0) else { misses.insert(key); return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        vector.draw(in: NSRect(x: 0, y: 0, width: px.w, height: px.h))
        NSGraphicsContext.restoreGraphicsState()
        rep.size = NSSize(width: px.w / 2, height: px.h / 2)
        let img = NSImage(size: rep.size)
        img.addRepresentation(rep)
        images[key] = img
        return img
    }

    // MARK: Zalo (Electron app.asar)

    private var zaloArchive: AsarArchive? {
        if let a = asar { return a }
        let a = appURL(.zalo).flatMap { AsarArchive(url: $0.appendingPathComponent("Contents/Resources/app.asar")) }
        asar = .some(a)
        return a
    }

    /// First file in `dir` of Zalo's web bundle whose name starts with `prefix` (names carry content hashes).
    func zaloFile(_ dir: String, prefix: String) -> Data? {
        zaloArchive?.firstFile(in: dir, prefix: prefix)
    }

    func zaloImage(_ prefix: String) -> NSImage? {
        let key = "zalo/\(prefix)"
        if let i = images[key] { return i }
        if misses.contains(key) { return nil }
        guard let d = zaloFile("pc-dist/assets", prefix: prefix), let img = NSImage(data: d) else {
            misses.insert(key); return nil
        }
        images[key] = img
        return img
    }

    /// Registers Zalo PC's icon font and its Segoe UI Variable text faces for this process.
    func registerZaloFonts() {
        guard !zaloFontsRegistered, let archive = zaloArchive else { return }
        for prefix in ["zalo-font.", "Zalo-Segoe-UI-Text.", "Zalo-Segoe-UI-Text-Semibold.", "Zalo-Segoe-UI.",
                       "Zalo-Segoe-UI-Semibold.", "Zalo-Segoe-UI-Bold."] {
            guard let data = archive.firstFile(in: "pc-dist/fonts", prefix: prefix, ext: "ttf"),
                  let provider = CGDataProvider(data: data as CFData),
                  let font = CGFont(provider) else { continue }
            CTFontManagerRegisterGraphicsFont(font, nil)
        }
        zaloFontsRegistered = NSFont(name: "zalo-font", size: 12) != nil
        Self.zaloFontsReady = zaloFontsRegistered
    }

    // MARK: Sounds

    enum SoundKind { case incoming, sent }

    func soundSource(_ p: Platform, _ kind: SoundKind) -> URL? {
        guard let app = appURL(p) else { return nil }
        let res = app.appendingPathComponent("Contents/Resources")
        let name: String?
        switch (p, kind) {
        case (.whatsapp, .incoming): name = "incoming_message_notification.m4a"
        case (.whatsapp, .sent): name = "send_message.aif"
        case (.telegram, .incoming): name = "100.m4a"
        case (.telegram, .sent): name = "sent.mp3"
        default: name = nil
        }
        guard let name else { return nil }
        let u = res.appendingPathComponent(name)
        return FileManager.default.fileExists(atPath: u.path) ? u : nil
    }

    private var players: [String: NSSound] = [:]

    func play(_ p: Platform, _ kind: SoundKind) {
        let key = "\(p.rawValue)-\(kind)"
        if players[key] == nil {
            if let u = soundSource(p, kind) {
                players[key] = NSSound(contentsOf: u, byReference: true)
            } else if kind == .incoming {
                players[key] = NSSound(named: p == .zalo ? "Pop" : "Tink")
            }
        }
        guard let s = players[key] else { return }
        s.stop()
        s.play()
    }

    /// Notification Center only plays CAF/AIFF/WAV from ~/Library/Sounds, so convert the app's tone
    /// once and return the file name to pass to `UNNotificationSound(named:)`.
    func notificationSoundName(_ p: Platform) -> String? {
        guard let src = soundSource(p, .incoming) else { return nil }
        let name = "ChatSpace-\(p.rawValue).caf"
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Sounds")
        let dst = dir.appendingPathComponent(name)
        if FileManager.default.fileExists(atPath: dst.path) { return name }
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let input = try AVAudioFile(forReading: src)
            let fmt = input.processingFormat
            let settings: [String: Any] = [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: fmt.sampleRate,
                AVNumberOfChannelsKey: fmt.channelCount, AVLinearPCMBitDepthKey: 16,
                AVLinearPCMIsFloatKey: false, AVLinearPCMIsBigEndianKey: false,
            ]
            let output = try AVAudioFile(forWriting: dst, settings: settings, commonFormat: fmt.commonFormat,
                                         interleaved: fmt.isInterleaved)
            let frames = AVAudioFrameCount(min(input.length, AVAudioFramePosition(fmt.sampleRate * 29)))
            guard let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: frames) else { return nil }
            try input.read(into: buf, frameCount: frames)
            try output.write(from: buf)
            return name
        } catch {
            try? FileManager.default.removeItem(at: dst)
            return nil
        }
    }
}

/// Minimal reader for Electron's asar archive format (JSON header + concatenated files).
final class AsarArchive {
    private let data: Data
    private let base: Int
    private let root: [String: Any]

    init?(url: URL) {
        guard let d = try? Data(contentsOf: url, options: .alwaysMapped), d.count > 16 else { return nil }
        func u32(_ o: Int) -> Int { d.subdata(in: o..<o + 4).withUnsafeBytes { Int($0.loadUnaligned(as: UInt32.self).littleEndian) } }
        let headerSize = u32(4)
        let jsonLen = u32(12)
        guard 16 + jsonLen <= d.count,
              let obj = try? JSONSerialization.jsonObject(with: d.subdata(in: 16..<16 + jsonLen)) as? [String: Any],
              let files = obj["files"] as? [String: Any] else { return nil }
        data = d
        base = 8 + headerSize
        root = files
    }

    private func directory(_ path: String) -> [String: Any]? {
        var node = root
        for part in path.split(separator: "/") {
            guard let next = (node[String(part)] as? [String: Any])?["files"] as? [String: Any] else { return nil }
            node = next
        }
        return node
    }

    func firstFile(in dir: String, prefix: String, ext: String? = nil) -> Data? {
        guard let entries = directory(dir) else { return nil }
        let name = entries.keys.sorted().first { k in k.hasPrefix(prefix) && (ext == nil || k.hasSuffix("." + ext!)) }
        guard let name, let entry = entries[name] as? [String: Any], entry["unpacked"] == nil,
              let size = entry["size"] as? Int,
              let offStr = entry["offset"] as? String, let off = Int(offStr) else { return nil }
        let start = base + off
        guard start + size <= data.count else { return nil }
        return data.subdata(in: start..<start + size)
    }
}

/// An image from an installed official app, falling back to an SF Symbol.
struct NativeIcon: View {
    let name: String
    let catalog: NativeAssets.Catalog
    var fallback: String
    var size: CGFloat? = nil
    var template = true

    var body: some View {
        if let img = NativeAssets.shared.image(name, catalog) {
            let base = Image(nsImage: img).renderingMode(template ? .template : .original).resizable().interpolation(.high)
            base.aspectRatio(contentMode: .fit)
                .frame(width: size ?? img.size.width, height: size.map { $0 * img.size.height / max(img.size.width, 1) } ?? img.size.height)
        } else {
            Image(systemName: fallback).font(.system(size: (size ?? 16) * 0.85))
        }
    }
}

/// Glyph from Zalo PC's `zalo-font` icon font, falling back to an SF Symbol.
struct ZaloGlyph: View {
    let code: UInt32
    var fallback: String
    var size: CGFloat = 24

    var body: some View {
        if NativeAssets.zaloFontsReady, let scalar = Unicode.Scalar(code) {
            Text(String(Character(scalar))).font(.custom("zalo-font", fixedSize: size))
        } else {
            Image(systemName: fallback).font(.system(size: size * 0.8))
        }
    }
}

/// Codepoints of the zalo-font glyphs used by the Zalo UI (from the font's SVG glyph names).
enum ZaloGlyphs {
    static let messageFilled: UInt32 = 0xEC5A, messageLine: UInt32 = 0xEC5B
    static let contactLine: UInt32 = 0xEABE, contactFilled: UInt32 = 0xEABD
    static let todoLine: UInt32 = 0xEDB7, cloudLine: UInt32 = 0xEAA5, settingLine: UInt32 = 0xED25
    static let search: UInt32 = 0xED16, addUser: UInt32 = 0xEA14, createGroup: UInt32 = 0xEB45
    static let emoji: UInt32 = 0xEA62, attach: UInt32 = 0xEA60, photo: UInt32 = 0xEA70
    static let sticker: UInt32 = 0xEA77, file: UInt32 = 0xEA63
    static let bellSlash: UInt32 = 0xEA2B, pin: UInt32 = 0xECCC
    static let check: UInt32 = 0xEA7B, doubleCheck: UInt32 = 0xEAE7
    static let call: UInt32 = 0xEA41, video: UInt32 = 0xEDF1
    static let quote: UInt32 = 0xEAD0, forward: UInt32 = 0xEACE, more: UInt32 = 0xEACF, reaction: UInt32 = 0xEAD1
    static let addGroupMember: UInt32 = 0xEA0F, sidebar: UInt32 = 0xED30, screenshot: UInt32 = 0xEA76
    static let namecard: UInt32 = 0xEC73, textFormat: UInt32 = 0xEDAC, moreH: UInt32 = 0xEC67
    static let emojiLine: UInt32 = 0xEAF9, quoteLine: UInt32 = 0xECEE, close: UInt32 = 0xEA9F
    static let chevronDown: UInt32 = 0xEA8C, like: UInt32 = 0xEC43, pinFilled: UInt32 = 0xECCE
    static let group: UInt32 = 0xEB44, briefcase: UInt32 = 0xEB4A, attachLine: UInt32 = 0xEA22
}

extension NSFont {
    static func zalo(_ size: CGFloat) -> NSFont {
        (NativeAssets.zaloFontsReady ? NSFont(name: "SegoeUIVariableStatic-1900000-a8000", size: size) : nil) ?? .systemFont(ofSize: size)
    }
}

extension Font {
    /// Zalo PC's UI typeface (Segoe UI Variable, shipped inside Zalo), or the system font.
    static func zalo(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        guard NativeAssets.zaloFontsReady else { return .system(size: size, weight: weight) }
        let name: String
        switch weight {
        case .semibold, .medium: name = size >= 20 ? "SegoeUIVariableStatic-2580000-240000" : "SegoeUIVariableStatic-2580000-a8000"
        case .bold, .heavy, .black: name = "SegoeUIVariableStatic-2bc0000-240000"
        default: name = size >= 20 ? "SegoeUIVariableStatic-1900000-240000" : "SegoeUIVariableStatic-1900000-a8000"
        }
        return .custom(name, fixedSize: size)
    }
}
