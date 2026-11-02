// Draws the ChatSpace app icon and writes an .icns.
// Usage: swift scripts/make-icon.swift <out.icns> [preview.png]
import AppKit

let S: CGFloat = 1024

func squircle(_ r: CGRect, n: CGFloat = 5) -> CGPath {
    let p = CGMutablePath()
    let a = r.width / 2, b = r.height / 2, cx = r.midX, cy = r.midY
    let steps = 720
    for i in 0...steps {
        let t = CGFloat(i) / CGFloat(steps) * 2 * .pi
        let c = cos(t), s = sin(t)
        let x = cx + a * copysign(pow(abs(c), 2 / n), c)
        let y = cy + b * copysign(pow(abs(s), 2 / n), s)
        i == 0 ? p.move(to: CGPoint(x: x, y: y)) : p.addLine(to: CGPoint(x: x, y: y))
    }
    p.closeSubpath()
    return p
}

func rgb(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

func gradient(_ colors: [CGColor], _ locs: [CGFloat]? = nil) -> CGGradient {
    CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: colors as CFArray, locations: locs)!
}

func draw(_ ctx: CGContext) {
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = squircle(body)

    // Drop shadow under the tile.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 30, color: rgb(0x000000, 0.35))
    ctx.addPath(shape); ctx.setFillColor(rgb(0x1B1A4A)); ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(shape); ctx.clip()

    // Deep-space background.
    ctx.drawLinearGradient(gradient([rgb(0x6A5CFF), rgb(0x3B2FB8), rgb(0x16123F)], [0, 0.5, 1]),
                           start: CGPoint(x: 260, y: 924), end: CGPoint(x: 760, y: 100),
                           options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    ctx.drawRadialGradient(gradient([rgb(0x9C8CFF, 0.55), rgb(0x9C8CFF, 0)]),
                           startCenter: CGPoint(x: 512, y: 560), startRadius: 0,
                           endCenter: CGPoint(x: 512, y: 560), endRadius: 420, options: [])

    // Stars.
    var seed: UInt64 = 0x5EED
    func rnd() -> CGFloat { seed = seed &* 6364136223846793005 &+ 1442695040888963407; return CGFloat(seed >> 33) / CGFloat(1 << 31) }
    for _ in 0..<46 {
        let x = 120 + rnd() * 784, y = 120 + rnd() * 784, r = 2 + rnd() * 4.5
        ctx.setFillColor(rgb(0xFFFFFF, 0.25 + rnd() * 0.6))
        ctx.fillEllipse(in: CGRect(x: x - r / 2, y: y - r / 2, width: r, height: r))
    }

    // Orbit geometry.
    let center = CGPoint(x: 512, y: 470)
    let ra: CGFloat = 345, rb: CGFloat = 118, rot: CGFloat = -16 * .pi / 180
    func orbit(_ t: CGFloat) -> CGPoint {
        let x = ra * cos(t), y = rb * sin(t)
        return CGPoint(x: center.x + x * cos(rot) - y * sin(rot), y: center.y + x * sin(rot) + y * cos(rot))
    }
    func ring(from: CGFloat, to: CGFloat) {
        let p = CGMutablePath()
        let n = 240
        for i in 0...n {
            let t = from + (to - from) * CGFloat(i) / CGFloat(n)
            i == 0 ? p.move(to: orbit(t)) : p.addLine(to: orbit(t))
        }
        ctx.addPath(p)
        ctx.setLineWidth(16); ctx.setLineCap(.butt)
        ctx.setStrokeColor(rgb(0xFFFFFF, 0.42)); ctx.strokePath()
    }
    func planet(_ t: CGFloat, _ r: CGFloat, _ colors: [CGColor]) {
        let c = orbit(t)
        let rect = CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r)
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 14, color: rgb(0x000000, 0.35))
        ctx.setFillColor(colors.last!); ctx.fillEllipse(in: rect)
        ctx.restoreGState()
        ctx.saveGState()
        ctx.addEllipse(in: rect); ctx.clip()
        ctx.drawLinearGradient(gradient(colors), start: CGPoint(x: rect.minX, y: rect.maxY),
                               end: CGPoint(x: rect.maxX, y: rect.minY), options: [])
        ctx.drawRadialGradient(gradient([rgb(0xFFFFFF, 0.65), rgb(0xFFFFFF, 0)]),
                               startCenter: CGPoint(x: c.x - r * 0.38, y: c.y + r * 0.4), startRadius: 0,
                               endCenter: CGPoint(x: c.x - r * 0.38, y: c.y + r * 0.4), endRadius: r * 0.9, options: [])
        ctx.restoreGState()
    }

    let zalo = [rgb(0x3D8BFF), rgb(0x0059E6)]
    let messenger = [rgb(0x00A2FF), rgb(0xA033FF), rgb(0xFF5280)]
    let whatsapp = [rgb(0x5BEA8B), rgb(0x1DB954)]
    let telegram = [rgb(0x5FD0FF), rgb(0x1E96D6)]

    // Back half of the orbit and the planet behind the bubble.
    ring(from: 0, to: .pi)
    planet(0.13 * .pi, 44, telegram)

    // The chat bubble.
    let bubble = CGMutablePath()
    let br = CGRect(x: 292, y: 400, width: 440, height: 350)
    bubble.addRoundedRect(in: br, cornerWidth: 158, cornerHeight: 158)
    let tail = CGMutablePath()
    tail.move(to: CGPoint(x: 360, y: 440))
    tail.addQuadCurve(to: CGPoint(x: 300, y: 338), control: CGPoint(x: 352, y: 380))
    tail.addQuadCurve(to: CGPoint(x: 452, y: 414), control: CGPoint(x: 400, y: 352))
    tail.closeSubpath()
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -18), blur: 36, color: rgb(0x0B0830, 0.55))
    ctx.addPath(bubble); ctx.addPath(tail); ctx.setFillColor(rgb(0xFFFFFF)); ctx.fillPath()
    ctx.restoreGState()
    ctx.saveGState()
    ctx.addPath(bubble); ctx.addPath(tail); ctx.clip()
    ctx.drawLinearGradient(gradient([rgb(0xFFFFFF), rgb(0xE4E1FF)]), start: CGPoint(x: 512, y: 750),
                           end: CGPoint(x: 512, y: 340), options: [])
    ctx.restoreGState()

    // Typing dots in the bubble.
    for (i, c) in [rgb(0x5A4BEF), rgb(0x6F61F5), rgb(0x8679FA)].enumerated() {
        let x = 418 + CGFloat(i) * 94, y: CGFloat = 575, r: CGFloat = 30
        ctx.setFillColor(c); ctx.fillEllipse(in: CGRect(x: x - r, y: y - r, width: 2 * r, height: 2 * r))
    }

    // Front half of the orbit with the remaining planets.
    ring(from: .pi, to: 2 * .pi)
    planet(0.97 * .pi, 50, zalo)
    planet(1.32 * .pi, 60, messenger)
    planet(1.72 * .pi, 54, whatsapp)

    // Glass edge highlight.
    ctx.restoreGState()
    ctx.saveGState()
    ctx.addPath(shape)
    ctx.setLineWidth(3); ctx.setStrokeColor(rgb(0xFFFFFF, 0.18)); ctx.strokePath()
    ctx.restoreGState()
}

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    let g = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.current = g
    let ctx = g.cgContext
    ctx.interpolationQuality = .high
    ctx.scaleBy(x: CGFloat(px) / S, y: CGFloat(px) / S)
    draw(ctx)
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let args = CommandLine.arguments
let out = URL(fileURLWithPath: args.count > 1 ? args[1] : "AppIcon.icns")
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("ChatSpace-\(UUID().uuidString).iconset")
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    try! render(base).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try! render(base * 2).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
if args.count > 2 { try! render(1024).write(to: URL(fileURLWithPath: args[2])) }
let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset.path, "-o", out.path]
try! p.run(); p.waitUntilExit()
try? FileManager.default.removeItem(at: iconset)
print(p.terminationStatus == 0 ? "wrote \(out.path)" : "iconutil failed")
