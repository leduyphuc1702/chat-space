import AppKit

/// Sample data for `CHATSPACE_DEMO=1`: renders every platform UI without accounts or bridges.
enum DemoData {
    static var enabled: Bool { ProcessInfo.processInfo.environment["CHATSPACE_DEMO"] != nil }

    /// A photo of a note with text in it, to exercise the viewer's Live Text.
    @MainActor
    private static func sampleImage() -> String {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("chatspace-demo-note.png").path
        if FileManager.default.fileExists(atPath: path) { return path }
        let size = NSSize(width: 1200, height: 800)
        let image = NSImage(size: size, flipped: true) { rect in
            NSColor(calibratedRed: 0.98, green: 0.96, blue: 0.88, alpha: 1).setFill()
            rect.fill()
            let lines = ["Góp ý thiết kế trang chủ", "", "1. Tăng khoảng cách giữa các khối lên 24px",
                         "2. Màu chủ đạo #0068FF, nút phụ màu xám", "3. Logo đặt bên trái thanh điều hướng",
                         "Hạn chót: thứ Sáu 15/11"]
            for (i, line) in lines.enumerated() {
                let font = NSFont.systemFont(ofSize: i == 0 ? 64 : 44, weight: i == 0 ? .bold : .regular)
                (line as NSString).draw(at: NSPoint(x: 80, y: 90 + CGFloat(i) * 100),
                                        withAttributes: [.font: font, .foregroundColor: NSColor(calibratedWhite: 0.15, alpha: 1)])
            }
            return true
        }
        if let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
           let png = rep.representation(using: .png, properties: [:]) {
            try? png.write(to: URL(fileURLWithPath: path))
        }
        return path
    }

    /// `CHATSPACE_DEMO_CURRENCY=GBP|EUR` switches the VietFlow sample PO and its invoice to that currency.
    static var currency: String { ProcessInfo.processInfo.environment["CHATSPACE_DEMO_CURRENCY"] ?? "USD" }

    /// `CHATSPACE_DEMO_INVOICE_CURRENCY` makes the invoice differ from the PO, e.g. a USD PO paid in £.
    static var invoiceCurrency: String { ProcessInfo.processInfo.environment["CHATSPACE_DEMO_INVOICE_CURRENCY"] ?? currency }

    /// How the sample invoice writes an amount: "$14.24", "£14.24", "14,24 €".
    static func invoiceMoney(_ v: Double) -> String {
        let n = String(format: "%.2f", abs(v))
        let s: String
        switch invoiceCurrency {
        case "GBP": s = "£" + n
        case "EUR": s = n.replacingOccurrences(of: ".", with: ",") + " €"
        default: s = "$" + n
        }
        return v < 0 ? "-" + s : s
    }

    /// A Vinted-style purchase receipt, for the VietFlow cost form.
    private static func sampleReceipt() -> String {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("chatspace-demo-receipt-\(invoiceCurrency).png").path
        if FileManager.default.fileExists(atPath: path) { return path }
        let size = NSSize(width: 900, height: 1100)
        let rows: [(String, String, Bool)] = [
            ("Your Vinted purchase receipt:", "", true), ("", "", false),
            ("Seller", "perfecttimingcloset", true), ("Order", "Victoria's Secret Ivory Satin Nightgown L", true),
            ("Paid", invoiceMoney(14.24), true), ("Item", invoiceMoney(11), false), ("Shipping", invoiceMoney(7.45), false),
            ("Vinted fee", invoiceMoney(1.25), false), ("Sales tax", invoiceMoney(0), false), ("Discount", invoiceMoney(-5.46), false),
            ("Payment method", "MasterCard, **********8440", true), ("Payment date", "10/30/2026 02:10 AM", true),
            ("Transaction ID", "23728847703", true),
        ]
        let image = NSImage(size: size, flipped: true) { rect in
            NSColor.white.setFill()
            rect.fill()
            for (i, row) in rows.enumerated() {
                let y = 70 + CGFloat(i) * 74
                let font = NSFont.systemFont(ofSize: 30, weight: row.2 ? .semibold : .regular)
                let color = NSColor(calibratedWhite: row.2 ? 0.1 : 0.45, alpha: 1)
                (row.0 as NSString).draw(at: NSPoint(x: 60, y: y), withAttributes: [.font: font, .foregroundColor: color])
                let value = row.1 as NSString
                let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 30), .foregroundColor: NSColor(calibratedWhite: 0.1, alpha: 1)]
                value.draw(at: NSPoint(x: 840 - value.size(withAttributes: attrs).width, y: y), withAttributes: attrs)
            }
            return true
        }
        if let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
           let png = rep.representation(using: .png, properties: [:]) {
            try? png.write(to: URL(fileURLWithPath: path))
        }
        return path
    }

    @MainActor
    static func load(into s: PlatformSession) {
        let now = Date().timeIntervalSince1970 * 1000
        let min = 60_000.0
        s.capabilities = ["reactions", "reply", "delete", "typing", "read_receipts", "media_send", "mentions", "edit", "mute", "pins"]
        if s.platform == .whatsapp || s.platform == .telegram { s.capabilities.insert("forward") }
        s.selfInfo = SelfInfo(id: "me", name: "Minh")
        s.connection = "online"

        func msg(_ id: String, _ chat: String, _ from: String?, _ name: String?, _ ago: Double, _ text: String,
                 kind: MessageKind = .text, status: DeliveryStatus? = nil, reactions: [Reaction]? = nil,
                 reply: ReplyRef? = nil, media: Media? = nil) -> Message {
            Message(id: id, chatId: chat, senderId: from ?? "me", senderName: name, fromMe: from == nil,
                    timestamp: now - ago * min, kind: kind, text: text, media: media, replyTo: reply,
                    reactions: reactions, status: from == nil ? (status ?? .read) : nil)
        }

        let direct = "c1", group = "c2"
        let conv: [Message] = [
            msg("m1", direct, "u1", "Lan Anh", 60 * 26, "Chào anh, mai mình họp lúc mấy giờ ạ?"),
            msg("m2", direct, nil, nil, 60 * 26 - 2, "9h sáng nhé em"),
            msg("m3", direct, nil, nil, 60 * 26 - 2.5, "Phòng họp tầng 3"),
            msg("m4", direct, "u1", "Lan Anh", 60 * 25, "Dạ vâng 👍", reactions: [Reaction(emoji: "❤️", count: 1, fromMe: true)]),
            msg("m5", direct, "u1", "Lan Anh", 34, "Anh ơi, em gửi bản thiết kế mới nhé. Anh xem giúp em phần màu sắc với bố cục trang chủ có ổn không ạ?"),
            msg("m6", direct, "u1", "Lan Anh", 33, "", kind: .file, media: Media(mime: "application/pdf", fileName: "ThietKe_TrangChu_v3.pdf", size: 2_480_000)),
            msg("m6b", direct, "u1", "Lan Anh", 32, "", kind: .image,
                reactions: [Reaction(emoji: "😆", count: 1, fromMe: false)],
                media: Media(mime: "image/png", path: sampleImage(), width: 1200, height: 800)),
            msg("m7", direct, nil, nil, 12, "Ok em, để anh xem rồi góp ý. Nhìn chung bố cục ổn rồi, chỉ cần chỉnh lại khoảng cách giữa các khối.",
                reply: ReplyRef(id: "m5", text: "Anh ơi, em gửi bản thiết kế mới nhé…", senderName: "Lan Anh")),
            msg("m8", direct, nil, nil, 11, "https://chatspace.vn/design-review", status: .delivered,
                reactions: [Reaction(emoji: "\u{2764}", count: 1, fromMe: false)]),
            msg("m9", direct, "u1", "Lan Anh", 2, "😂"),
        ]
        let groupConv: [Message] = [
            Message(id: "g0", chatId: group, senderId: "sys", fromMe: false, timestamp: now - 90 * min, kind: .system, text: "Tuấn đã thêm Hà vào nhóm"),
            msg("g1", group, "u2", "Tuấn Nguyễn", 80, "Cuối tuần này team đi cafe không mọi người?"),
            msg("g2", group, "u3", "Hà Phạm", 78, "Đi đi! Mình rảnh chiều thứ 7", reactions: [Reaction(emoji: "👍", count: 3, fromMe: true)]),
            msg("g3", group, "u3", "Hà Phạm", 77.5, "Quán cũ ở Phan Xích Long nhé"),
            msg("g4", group, nil, nil, 70, "Mình ok, 3h chiều nha"),
            msg("g5", group, "u2", "Tuấn Nguyễn", 5, "Chốt 3h thứ 7 ☕️", reactions: [Reaction(emoji: "❤️", count: 2, fromMe: false), Reaction(emoji: "😆", count: 1, fromMe: false)]),
        ]

        s.messages = [direct: conv, group: groupConv]
        s.reachedStart = [direct, group]
        s.chats = [
            Chat(id: direct, title: "Lan Anh", kind: .direct, unread: 1, timestamp: now - 2 * min, lastMessage: conv.last, online: true),
            Chat(id: group, title: "Team Thiết Kế", kind: .group, unread: 3, timestamp: now - 5 * min, lastMessage: groupConv.last, memberCount: 8),
            Chat(id: "c3", title: "Hoàng Long", kind: .direct, unread: 0, pinned: true, timestamp: now - 60 * 3 * min,
                 lastMessage: msg("x1", "c3", nil, nil, 180, "Cảm ơn bạn nhiều nhé!"), lastSeen: now - 40 * min),
            Chat(id: "c4", title: "Gia đình 🏡", kind: .group, unread: 12, mutedUntil: -1, timestamp: now - 60 * 20 * min,
                 lastMessage: msg("x2", "c4", "u5", "Mẹ", 1200, "", kind: .image), memberCount: 5),
            Chat(id: "c5", title: "Phương Thảo", kind: .direct, unread: 0, markedUnread: true, timestamp: now - 60 * 50 * min,
                 lastMessage: msg("x3", "c5", "u6", "Phương Thảo", 3000, "Hẹn gặp lại nha")),
            Chat(id: "c6", title: "Kênh Tin Công Nghệ", kind: .channel, unread: 0, timestamp: now - 60 * 72 * min,
                 lastMessage: msg("x4", "c6", "u7", nil, 4320, "Apple ra mắt macOS mới với nhiều tính năng AI")),
        ]
        for i in s.chats.indices where s.chats[i].kind == .direct { s.chats[i].isContact = s.chats[i].id != "c5" }

        // Per-platform chats, so the ChatSpace inbox and the "VF" custom group have something to merge.
        let extra: [(String, String, ChatKind, Int, Double, String, String?)] = {
            switch s.platform {
            case .zalo: return [("z1", "VF 1 - Kho Hà Nội", .group, 4, 7, "Đã nhập 120 thùng hàng", "Minh Quân"),
                                ("z2", "VF-2 Giao hàng", .group, 0, 300, "Xe đã xuất bến", "Tài xế Hùng")]
            case .messenger: return [("m1", "VF3 Marketing", .group, 2, 15, "Banner mới đây nha", "Thu Trang"),
                                     ("m2", "Ngọc Ánh", .direct, 1, 45, "Tối nay đi ăn không?", nil)]
            case .whatsapp: return [("w1", "VF Logistics", .group, 0, 600, "Shipment ETA Monday", "David"),
                                    ("w2", "Anh Tuấn (Úc)", .direct, 3, 25, "Gọi lại cho anh nhé", nil)]
            case .telegram: return [("t1", "VF-5 Dev", .group, 7, 3, "Build #482 passed ✅", "CI Bot"),
                                    ("t2", "BotFather", .bot, 0, 900, "Done! Congratulations on your new bot.", nil),
                                    ("t3", "Crypto Signals", .channel, 25, 9, "BTC breaks 120k", nil)]
            }
        }()
        for (id, title, kind, unread, ago, text, sender) in extra {
            let last = msg("l" + id, id, sender == nil ? "p" + id : "s" + id, sender ?? title, ago, text)
            var c = Chat(id: id, title: title, kind: kind, unread: unread, timestamp: now - ago * min, lastMessage: last,
                         memberCount: kind == .group ? 12 : nil)
            c.isContact = kind == .direct
            if s.platform == .telegram && id == "t3" { c.mutedUntil = -1 }
            s.chats.append(c)
            let them = sender == nil ? "p" + id : "s" + id
            s.messages[id] = [
                msg("a" + id, id, them, sender ?? title, ago + 40, "Chào cả nhà 👋"),
                msg("b" + id, id, nil, nil, ago + 30, "Ok, mình đang theo dõi nhé"),
                msg("c" + id, id, them, sender ?? title, ago + 10, "Cập nhật tiến độ trong hôm nay"),
                last,
            ]
            s.reachedStart.insert(id)
        }
        if s.platform == .telegram {
            let vf = "t4"
            let po = msg("v1", vf, "u8", "Anh Tran", 70, "POVFMANNHI0082 Total: \(invoiceMoney(14.24)) https://www.vinted.com/items/10577605590-victorias-secret-ivory-satin-lace-trim-slip-nightgown-l\nChị mua giúp em ạ @Minh Ly")
            let vfConv = [
                po,
                msg("v2", vf, "u9", "Minh Ly Dang", 64, "", kind: .image,
                    reply: ReplyRef(id: po.id, text: po.text, senderName: po.senderName),
                    media: Media(mime: "image/png", path: sampleReceipt(), width: 900, height: 1100)),
                msg("v3", vf, "u9", "Minh Ly Dang", 63, "Usdt 26350"),
            ]
            s.messages[vf] = vfConv
            s.reachedStart.insert(vf)
            s.chats.append(Chat(id: vf, title: "VF - Đơn hàng ngày", kind: .group, unread: 0, timestamp: now - 63 * min,
                                lastMessage: vfConv.last, memberCount: 11))
        }
        s.chats.sort { ($0.isPinned ? 1 : 0, $0.timestamp) > ($1.isPinned ? 1 : 0, $1.timestamp) }

        if s.platform == .telegram {
            s.capabilities.insert("folders")
            s.folders = [
                ChatFolder(id: "1", title: "Công việc", emoticon: "💼", includeChatIds: ["c2", "t1"]),
                ChatFolder(id: "2", title: "Cá nhân", emoticon: "👤", excludeChatIds: ["c5"], contacts: true, nonContacts: true),
                ChatFolder(id: "3", title: "Nhóm", groups: true),
                ChatFolder(id: "4", title: "Kênh", channels: true),
                ChatFolder(id: "5", title: "Bot", bots: true),
                ChatFolder(id: "6", title: "Chưa đọc", contacts: true, nonContacts: true, groups: true, channels: true, bots: true, excludeRead: true),
            ]
            if let f = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_FOLDER"] { s.selectedFolderId = f }
        }

        s.selectedChatId = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_EMPTY"] != nil ? nil
            : (s.platform == .telegram || s.platform == .whatsapp ? group : direct)
        s.typing = s.platform == .zalo ? ["c3": [TypingInfo(userId: "u4", name: "Hoàng Long")]] : [:]
        if let id = s.selectedChatId { s.loadPins(id) }
        s.authState = .loggedIn
        if let delay = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_NOTIFY"].flatMap(Double.init),
           s.platform == ProcessInfo.processInfo.environment["CHATSPACE_DEMO_NOTIFY_PLATFORM"].flatMap(Platform.init(rawValue:)) ?? .whatsapp {
            // CHATSPACE_DEMO_NOTIFY_COUNT=n repeats it n times, `delay` seconds apart.
            let count = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_NOTIFY_COUNT"].flatMap(Int.init) ?? 1
            let target = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_NOTIFY_CHAT"] ?? "c3"
            let texts = ["Tối nay rảnh không? Đi ăn lẩu nhé 🍲", "7 giờ ở quán cũ nha", "Nhớ rủ cả Minh với Lan luôn"]
            for i in 0..<max(1, count) { Task { @MainActor in
                try? await Task.sleep(nanoseconds: UInt64(delay * Double(i + 1) * 1_000_000_000))
                let m = Message(id: "live-\(i)-\(Int(Date().timeIntervalSince1970))", chatId: target, senderId: "u4", senderName: "Hoàng Long",
                                fromMe: false, timestamp: Date().timeIntervalSince1970 * 1000, kind: .text,
                                text: texts[i % texts.count])
                guard let data = try? JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(["message": m])) else { return }
                var payload = data
                if case .object(var o) = payload { o["live"] = .bool(true); payload = .object(o) }
                s.injectDemoEvent("message.new", payload)
                if var chat = s.chats.first(where: { $0.id == target }) {
                    chat.unread += 1
                    chat.timestamp = m.timestamp
                    chat.lastMessage = m
                    let c = try? JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(["chat": chat]))
                    if let c { s.injectDemoEvent("chat.upsert", c) }
                }
            } }
        }
        if s.platform == .messenger, ProcessInfo.processInfo.environment["CHATSPACE_DEMO_HISTORY_PIN"] != nil {
            s.capabilities.insert("history_pin")
            s.historyBackup = .locked
        }
        if s.platform == .zalo, let raw = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_PHONE_SYNC"] {
            s.capabilities.insert("phone_sync")
            if let state = PhoneSyncProgress.State(rawValue: raw), state != .idle {
                s.phoneSync = PhoneSyncProgress(state: state, chats: 87, chatsDone: 30, messages: state == .done ? 12_480 : 4_215,
                                                skipped: state == .done ? 2 : nil, code: state == .failed ? "rejected" : nil)
            }
        }
        if s.platform == .messenger, let screen = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_LOGIN"] {
            loginScreen(screen, into: s)
        }
    }

    /// `CHATSPACE_DEMO_LOGIN=form|cookie|code|select|wait` shows Messenger's onboarding at that step;
    /// `CHATSPACE_DEMO_LOGIN_ERROR` adds an error under it.
    @MainActor
    private static func loginScreen(_ screen: String, into s: PlatformSession) {
        if screen != "cookie" { s.capabilities.insert("password_login") }
        s.authState = .loggedOut
        s.authError = ProcessInfo.processInfo.environment["CHATSPACE_DEMO_LOGIN_ERROR"]
        let steps: [String: AuthStep] = [
            "code": AuthStep(stepId: "demo-2fa", kind: "input",
                             instructions: "Nhập mã gồm 6 chữ số từ ứng dụng xác thực hoặc tin nhắn SMS gửi tới số điện thoại của bạn.",
                             fields: [.init(id: "code", name: "Mã đăng nhập", type: "code")], canCancel: true),
            "select": AuthStep(stepId: "demo-select", kind: "input", instructions: "Chọn cách xác nhận đây là bạn",
                               fields: [.init(id: "method", name: "Phương thức", type: "select",
                                              options: ["Ứng dụng xác thực", "Tin nhắn SMS", "Phê duyệt trên thiết bị khác"])],
                               canCancel: true),
            "wait": AuthStep(stepId: "demo-wait", kind: "wait",
                             instructions: "Kiểm tra thông báo trên điện thoại hoặc máy tính khác đang đăng nhập Facebook và chọn \"Đúng, là tôi\".",
                             canCancel: true),
        ]
        if let step = steps[screen] {
            s.authStep = step
            s.authState = .awaitingInput
        }
    }

    /// "Team Thiết Kế" has two pins (the meeting place, then the time), "Lan Anh" the design file.
    @MainActor
    static func pins(_ chatId: String, in s: PlatformSession) -> [Message] {
        let ids = ["c2": ["g5", "g3"], "c1": ["m6"]][chatId] ?? []
        return ids.compactMap { id in s.messages[chatId]?.first { $0.id == id } }
    }

    static func members(_ chatId: String) -> [ChatMember] {
        guard chatId == "c2" else { return [ChatMember(id: "u1", name: "Lan Anh")] }
        return [
            ChatMember(id: "u2", name: "Tuấn Nguyễn"), ChatMember(id: "u3", name: "Hà Phạm"),
            ChatMember(id: "u4", name: "Đức Trần"), ChatMember(id: "u5", name: "Hương Lê"),
            ChatMember(id: "u6", name: "Quân Võ"), ChatMember(id: "u7", name: "Thảo Phương"),
            ChatMember(id: "me", name: "Minh"),
        ]
    }
}
