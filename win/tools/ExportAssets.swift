// Exports everything the macOS build reads from the system or from installed apps at runtime, so the
// Windows build can show the same pixels: SF Symbols (alpha masks per weight), WhatsApp/Telegram asset
// catalog images, Telegram's wallpaper pattern, Zalo PC's fonts and artwork, app icons and sounds.
//
// usage: swift ExportAssets.swift <repo root> <output dir>
import AppKit
import AVFoundation

let args = CommandLine.arguments
guard args.count == 3 else { fatalError("usage: ExportAssets <repo root> <out dir>") }
let root = URL(fileURLWithPath: args[1])
let out = URL(fileURLWithPath: args[2])
let fm = FileManager.default
try? fm.removeItem(at: out)
try fm.createDirectory(at: out, withIntermediateDirectories: true)

func dir(_ name: String) -> URL {
    let d = out.appendingPathComponent(name, isDirectory: true)
    try? fm.createDirectory(at: d, withIntermediateDirectories: true)
    return d
}

func writePNG(_ rep: NSBitmapImageRep, _ url: URL) {
    try! rep.representation(using: .png, properties: [:])!.write(to: url)
}

/// Draws `image` into a bitmap of `size` points at `scale` pixels per point.
func render(_ size: NSSize, scale: CGFloat, _ draw: () -> Void) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: max(1, Int((size.width * scale).rounded(.up))),
                               pixelsHigh: max(1, Int((size.height * scale).rounded(.up))), bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = size
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw()
    NSGraphicsContext.restoreGraphicsState()
    return rep
}

// MARK: String literals in the Swift sources

var literals = Set<String>()
let srcDir = root.appendingPathComponent("app/Sources")
let re = try NSRegularExpression(pattern: "\"([A-Za-z0-9_.\\-]+)\"")
for case let f as URL in fm.enumerator(at: srcDir, includingPropertiesForKeys: nil)! where f.pathExtension == "swift" {
    let s = try String(contentsOf: f, encoding: .utf8)
    for m in re.matches(in: s, range: NSRange(s.startIndex..., in: s)) {
        literals.insert((s as NSString).substring(with: m.range(at: 1)))
    }
}
// Built dynamically ("Icon_Sidebar_" + name).
for n in ["Animal", "Book", "Coin", "Game", "Home", "Lamp", "Like", "Lock", "Love", "Mask", "Math", "Music", "Paint",
          "Plane", "Sport", "Star", "Student", "Telegram", "Personal", "Group", "AllChats", "Read", "Unmuted", "Folder",
          "Flash", "Channel", "Bot", "Wine", "Work", "Unread"] {
    literals.insert("Icon_Sidebar_" + n)
}

// MARK: SF Symbols

let symbolDir = dir("symbols")
let weights: [(String, NSFont.Weight)] = [("ultraLight", .ultraLight), ("thin", .thin), ("light", .light), ("regular", .regular),
                                          ("medium", .medium), ("semibold", .semibold), ("bold", .bold), ("heavy", .heavy), ("black", .black)]
let symbolPoint: CGFloat = 64
let paletteSymbols: Set<String> = ["xmark.circle.fill"]
var symbolManifest: [String: [String: [String: Double]]] = [:]
for name in literals.sorted() where name.first?.isLowercase == true || name.first?.isNumber == true {
    guard NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil else { continue }
    var entry: [String: [String: Double]] = [:]
    for (wname, w) in weights {
        let cfg = NSImage.SymbolConfiguration(pointSize: symbolPoint, weight: w)
        guard let img = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(cfg) else { continue }
        let size = img.size
        let rep = render(size, scale: 2) { img.draw(in: NSRect(origin: .zero, size: size)) }
        writePNG(rep, symbolDir.appendingPathComponent("\(name).\(wname).png"))
        let a = img.alignmentRect
        entry[wname] = ["w": size.width, "h": size.height, "ax": a.minX, "ay": a.minY, "aw": a.width, "ah": a.height]
        if paletteSymbols.contains(name) {
            for (i, colors) in [[NSColor.black, NSColor.clear], [NSColor.clear, NSColor.black]].enumerated() {
                let pc = cfg.applying(NSImage.SymbolConfiguration(paletteColors: colors))
                guard let layer = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(pc) else { continue }
                writePNG(render(size, scale: 2) { layer.draw(in: NSRect(origin: .zero, size: size)) },
                         symbolDir.appendingPathComponent("\(name).\(wname).p\(i).png"))
            }
        }
    }
    symbolManifest[name] = entry
}
try JSONSerialization.data(withJSONObject: ["pointSize": symbolPoint, "symbols": symbolManifest], options: [.sortedKeys])
    .write(to: symbolDir.appendingPathComponent("symbols.json"))
print("symbols:", symbolManifest.count)

// MARK: Installed apps

func app(_ ids: [String]) -> URL? {
    for id in ids { if let u = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id) { return u } }
    return nil
}
let whatsapp = app(["net.whatsapp.WhatsApp", "desktop.WhatsApp"])
let telegram = app(["ru.keepcoder.Telegram", "org.telegram.desktop"])
let zalo = app(["com.vng.zalo"])

var catalogManifest: [String: [String: [String: Double]]] = [:]
func exportCatalog(_ key: String, _ bundle: Bundle?) {
    guard let bundle else { print("missing catalog", key); return }
    let d = dir(key)
    var entries: [String: [String: Double]] = [:]
    for name in literals.sorted() {
        guard let img = bundle.image(forResource: name) else { continue }
        let size = img.size
        guard size.width > 0, size.height > 0 else { continue }
        // 3x covers 150 %/200 % Windows scaling and the zoomed sizes the UI asks for.
        let scale: CGFloat = max(size.width, size.height) > 600 ? 1 : 3
        writePNG(render(size, scale: scale) { img.draw(in: NSRect(origin: .zero, size: size)) }, d.appendingPathComponent(name + ".png"))
        entries[name] = ["w": size.width, "h": size.height, "template": img.isTemplate ? 1 : 0]
    }
    catalogManifest[key] = entries
    print(key, entries.count)
}
exportCatalog("whatsapp", whatsapp.flatMap { Bundle(url: $0) })
exportCatalog("whatsappShared", whatsapp.flatMap { Bundle(url: $0.appendingPathComponent("Contents/Frameworks/SharedModules.framework")) })
exportCatalog("telegram", telegram.flatMap { Bundle(url: $0) })
try JSONSerialization.data(withJSONObject: catalogManifest, options: [.sortedKeys])
    .write(to: out.appendingPathComponent("catalogs.json"))

// Telegram doodle pattern (gzipped SVG), rasterised at full size; shown at half size.
if let url = telegram?.appendingPathComponent("Contents/Resources/builtin-wallpaper-svg"),
   let gz = try? Data(contentsOf: url), gz.count > 18, gz[0] == 0x1F, gz[1] == 0x8B,
   let svg = try? (gz.subdata(in: 10..<gz.count - 8) as NSData).decompressed(using: .zlib) as Data,
   let vector = NSImage(data: svg) {
    let size = vector.size
    writePNG(render(size, scale: 1) { vector.draw(in: NSRect(origin: .zero, size: size)) },
             dir("telegram").appendingPathComponent("pattern.png"))
    print("telegram pattern", size)
}

// App icons as the Dock shows them.
let icons = dir("icons")
for (name, url) in [("whatsapp", whatsapp), ("telegram", telegram), ("zalo", zalo)] {
    guard let url else { continue }
    let icon = NSWorkspace.shared.icon(forFile: url.path)
    let size = NSSize(width: 128, height: 128)
    writePNG(render(size, scale: 2) { icon.draw(in: NSRect(origin: .zero, size: size)) }, icons.appendingPathComponent(name + ".png"))
}
let appIcon = NSImage(contentsOf: root.appendingPathComponent("app/Resources/AppIcon.icns"))!
for px in [16, 24, 32, 48, 64, 128, 256] {
    let size = NSSize(width: px, height: px)
    writePNG(render(size, scale: 1) { appIcon.draw(in: NSRect(origin: .zero, size: size)) },
             icons.appendingPathComponent("chatspace-\(px).png"))
}
writePNG(render(NSSize(width: 128, height: 128), scale: 2) { appIcon.draw(in: NSRect(x: 0, y: 0, width: 128, height: 128)) },
         icons.appendingPathComponent("chatspace.png"))

// MARK: Zalo PC (Electron app.asar): fonts and artwork

if let zalo {
    let asarURL = zalo.appendingPathComponent("Contents/Resources/app.asar")
    let d = try Data(contentsOf: asarURL, options: .alwaysMapped)
    func u32(_ o: Int) -> Int { d.subdata(in: o..<o + 4).withUnsafeBytes { Int($0.loadUnaligned(as: UInt32.self).littleEndian) } }
    let headerSize = u32(4), jsonLen = u32(12)
    let header = try JSONSerialization.jsonObject(with: d.subdata(in: 16..<16 + jsonLen)) as! [String: Any]
    let base = 8 + headerSize
    func entries(_ path: String) -> [String: Any] {
        var node = header["files"] as! [String: Any]
        for part in path.split(separator: "/") { node = (node[String(part)] as! [String: Any])["files"] as! [String: Any] }
        return node
    }
    func file(_ dirPath: String, prefix: String, ext: String? = nil) -> Data? {
        let e = entries(dirPath)
        guard let name = e.keys.sorted().first(where: { $0.hasPrefix(prefix) && (ext == nil || $0.hasSuffix("." + ext!)) }),
              let entry = e[name] as? [String: Any], let size = entry["size"] as? Int,
              let off = (entry["offset"] as? String).flatMap(Int.init) else { return nil }
        return d.subdata(in: base + off..<base + off + size)
    }
    let zd = dir("zalo")
    for prefix in ["zalo-font.", "Zalo-Segoe-UI-Text.", "Zalo-Segoe-UI-Text-Semibold.", "Zalo-Segoe-UI.",
                   "Zalo-Segoe-UI-Semibold.", "Zalo-Segoe-UI-Bold."] {
        if let f = file("pc-dist/fonts", prefix: prefix, ext: "ttf") {
            try f.write(to: zd.appendingPathComponent(prefix + "ttf"))
        }
    }
    for prefix in ["logo-new.", "inapp-welcome-screen-01.", "inapp-welcome-screen-02.", "inapp-welcome-screen-03."] {
        guard let f = file("pc-dist/assets", prefix: prefix), let img = NSImage(data: f) else { print("zalo missing", prefix); continue }
        // Re-encode (SVG/WebP sources) to PNG at 2x of the asset's point size.
        let size = img.size
        writePNG(render(size, scale: 2) { img.draw(in: NSRect(origin: .zero, size: size)) }, zd.appendingPathComponent(prefix + "png"))
    }
    print("zalo ok")
}

// MARK: Sounds → 16-bit WAV (Windows plays these without codecs)

let sd = dir("sounds")
func toWav(_ src: URL, _ name: String) {
    guard fm.fileExists(atPath: src.path) else { print("no sound", src.path); return }
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/afconvert")
    p.arguments = ["-f", "WAVE", "-d", "LEI16", src.path, sd.appendingPathComponent(name + ".wav").path]
    try! p.run(); p.waitUntilExit()
}
if let whatsapp {
    toWav(whatsapp.appendingPathComponent("Contents/Resources/incoming_message_notification.m4a"), "whatsapp-incoming")
    toWav(whatsapp.appendingPathComponent("Contents/Resources/send_message.aif"), "whatsapp-sent")
}
if let telegram {
    toWav(telegram.appendingPathComponent("Contents/Resources/100.m4a"), "telegram-incoming")
    toWav(telegram.appendingPathComponent("Contents/Resources/sent.mp3"), "telegram-sent")
}
toWav(URL(fileURLWithPath: "/System/Library/Sounds/Pop.aiff"), "pop")
toWav(URL(fileURLWithPath: "/System/Library/Sounds/Tink.aiff"), "tink")
print("done")
