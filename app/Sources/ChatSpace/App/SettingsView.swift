import SwiftUI

/// Settings laid out like Telegram for macOS: a list of coloured-icon rows on the left, grouped
/// cards on a grey background on the right.
struct SettingsView: View {
    @Environment(AppModel.self) private var model
    @AppStorage("settings.section") private var sectionRaw = SettingsSection.storage.rawValue
    @State private var search = ""

    private var section: SettingsSection { SettingsSection(rawValue: sectionRaw) ?? .storage }

    var body: some View {
        HStack(spacing: 0) {
            sidebar.frame(width: 250)
            Rectangle().fill(TGS.separator).frame(width: 1)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    Text(section.title).font(.system(size: 20, weight: .semibold)).padding(.horizontal, 22).padding(.top, 18).padding(.bottom, 4)
                    detail
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.bottom, 24)
            }
            .background(TGS.background)
        }
        .frame(width: 860, height: 640)
        .environment(\.colorScheme, .light)
    }

    // MARK: Sidebar

    private var sidebar: some View {
        VStack(spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(TGS.secondary)
                TextField("Tìm kiếm", text: $search).textFieldStyle(.plain).font(.system(size: 13))
            }
            .padding(.horizontal, 10).frame(height: 28)
            .background(RoundedRectangle(cornerRadius: 14).fill(Color(hex: 0xF1F1F4)))
            .padding(10)
            ScrollView {
                VStack(spacing: 0) {
                    profile
                    ForEach(SettingsSection.sidebarGroups.indices, id: \.self) { gi in
                        let rows = SettingsSection.sidebarGroups[gi].filter { search.isEmpty || $0.title.localizedCaseInsensitiveContains(search) }
                        if !rows.isEmpty {
                            Rectangle().fill(TGS.separator).frame(height: 1).padding(.vertical, 6).padding(.leading, 56)
                            ForEach(rows) { sidebarRow($0) }
                        }
                    }
                }
            }
        }
        .background(Color.white)
    }

    private var profile: some View {
        HStack(spacing: 12) {
            ChatSpaceIcon(size: 50)
            VStack(alignment: .leading, spacing: 2) {
                Text("ChatSpace").font(.system(size: 15, weight: .semibold))
                let n = Platform.allCases.filter { model.session($0).authState == .loggedIn }.count
                Text("\(n)/\(Platform.allCases.count) nền tảng đã đăng nhập").font(.system(size: 12)).foregroundStyle(TGS.secondary)
            }
            Spacer()
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .background(section == .accounts ? TGS.accent : .clear)
        .foregroundStyle(section == .accounts ? .white : .primary)
        .contentShape(Rectangle())
        .onTapGesture { sectionRaw = SettingsSection.accounts.rawValue }
    }

    private func sidebarRow(_ s: SettingsSection) -> some View {
        let on = section == s
        return HStack(spacing: 12) {
            RoundedRectangle(cornerRadius: 7, style: .continuous).fill(s.color)
                .frame(width: 28, height: 28)
                .overlay(Image(systemName: s.icon).font(.system(size: 14, weight: .medium)).foregroundStyle(.white))
            Text(s.title).font(.system(size: 13.5))
            Spacer()
            if let badge = badge(for: s) {
                Text(badge).font(.system(size: 11, weight: .semibold)).foregroundStyle(on ? TGS.accent : .white)
                    .padding(.horizontal, 6).frame(minHeight: 18)
                    .background(Capsule().fill(on ? .white : TGS.accent))
            }
        }
        .padding(.horizontal, 14).frame(height: 40)
        .background(on ? TGS.accent : .clear)
        .foregroundStyle(on ? .white : .primary)
        .contentShape(Rectangle())
        .onTapGesture { sectionRaw = s.rawValue }
    }

    private func badge(for s: SettingsSection) -> String? {
        switch s {
        case .ignored: Workspace.shared.ignored.isEmpty ? nil : "\(Workspace.shared.ignored.count)"
        case .groups: Workspace.shared.groups.isEmpty ? nil : "\(Workspace.shared.groups.count)"
        default: nil
        }
    }

    // MARK: Detail

    @ViewBuilder private var detail: some View {
        switch section {
        case .accounts: AccountsSettings()
        case .notifications: NotificationSettings()
        case .storage: StorageSettingsPane()
        case .groups: GroupsSettings()
        case .ignored: IgnoredSettings()
        case .bubbles: BubbleSettingsPane()
        case .translation: TranslationSettingsPane()
        case .composer: ComposerSettingsPane()
        case .telegram: TelegramAPISettings()
        case .assets: AssetsSettings()
        case .updates: UpdateSettings()
        }
    }
}

enum SettingsSection: String, CaseIterable, Identifiable {
    case accounts, notifications, storage, groups, ignored, bubbles, translation, composer, telegram, assets, updates
    var id: String { rawValue }

    static let sidebarGroups: [[SettingsSection]] = [[.notifications, .storage], [.groups, .ignored, .bubbles, .translation, .composer], [.telegram, .assets, .updates]]

    var title: String {
        switch self {
        case .accounts: "Tài khoản"
        case .notifications: "Thông báo và âm thanh"
        case .storage: "Dữ liệu và bộ nhớ"
        case .groups: "Nhóm tuỳ chỉnh"
        case .ignored: "Đã bỏ qua"
        case .bubbles: "Bong bóng chat"
        case .translation: "Dịch tin nhắn"
        case .composer: "Soạn tin nhắn"
        case .telegram: "Telegram API"
        case .assets: "Tài nguyên gốc"
        case .updates: "Cập nhật"
        }
    }

    var icon: String {
        switch self {
        case .accounts: "person.crop.circle.fill"
        case .notifications: "bell.badge.fill"
        case .storage: "cylinder.split.1x2.fill"
        case .groups: "folder.fill"
        case .ignored: "eye.slash.fill"
        case .bubbles: "bubble.left.and.bubble.right.fill"
        case .translation: "translate"
        case .composer: "function"
        case .telegram: "paperplane.fill"
        case .assets: "square.stack.3d.up.fill"
        case .updates: "arrow.down.circle.fill"
        }
    }

    var color: Color {
        switch self {
        case .accounts: Color(hex: 0x3390EC)
        case .notifications: Color(hex: 0xFF3B30)
        case .storage: Color(hex: 0x34C759)
        case .groups: Color(hex: 0x5E5CE6)
        case .ignored: Color(hex: 0xFF9500)
        case .bubbles: Color(hex: 0x32ADE6)
        case .translation: Color(hex: 0x30B0C7)
        case .composer: Color(hex: 0xAF52DE)
        case .telegram: Color(hex: 0x2AABEE)
        case .assets: Color(hex: 0x8E8E93)
        case .updates: Color(hex: 0x34C759)
        }
    }
}

/// Telegram settings palette.
enum TGS {
    static let accent = Color(hex: 0x3390EC)
    static let background = Color(hex: 0xEFEFF4)
    static let secondary = Color(hex: 0x8E8E93)
    static let separator = Color(hex: 0xE3E3E6)
}

// MARK: Building blocks

/// A white rounded card with Telegram's uppercase grey header and footer.
struct TGSection<Content: View>: View {
    var header: String?
    var footer: String?
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let header {
                Text(header.uppercased()).font(.system(size: 11.5)).foregroundStyle(TGS.secondary).padding(.horizontal, 14)
            }
            VStack(spacing: 0) { content() }
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.white))
            if let footer {
                Text(footer).font(.system(size: 11.5)).foregroundStyle(TGS.secondary).padding(.horizontal, 14)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, 22).padding(.top, 16)
    }
}

/// One row inside a TGSection; rows after the first get a hairline separator.
struct TGRow<Trailing: View>: View {
    var icon: String? = nil
    var iconColor: Color = TGS.accent
    var title: String
    var subtitle: String? = nil
    var first = false
    var titleColor: Color = .primary
    @ViewBuilder var trailing: () -> Trailing

    var body: some View {
        HStack(spacing: 12) {
            if let icon {
                RoundedRectangle(cornerRadius: 6, style: .continuous).fill(iconColor).frame(width: 24, height: 24)
                    .overlay(Image(systemName: icon).font(.system(size: 12, weight: .medium)).foregroundStyle(.white))
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 13.5)).foregroundStyle(titleColor)
                if let subtitle { Text(subtitle).font(.system(size: 11.5)).foregroundStyle(TGS.secondary).lineLimit(2) }
            }
            Spacer(minLength: 8)
            trailing()
        }
        .padding(.horizontal, 14).frame(minHeight: 42)
        .overlay(alignment: .top) {
            if !first { Rectangle().fill(TGS.separator).frame(height: 1).padding(.leading, icon == nil ? 14 : 50) }
        }
    }
}

extension TGRow where Trailing == EmptyView {
    init(icon: String? = nil, iconColor: Color = TGS.accent, title: String, subtitle: String? = nil, first: Bool = false, titleColor: Color = .primary) {
        self.init(icon: icon, iconColor: iconColor, title: title, subtitle: subtitle, first: first, titleColor: titleColor) { EmptyView() }
    }
}

/// Telegram's stepped slider: a blue track with dots and the labels of each stop.
struct TickSlider<V: Hashable>: View {
    let stops: [(V, String)]
    @Binding var value: V

    var body: some View {
        let index = stops.firstIndex { $0.0 == value } ?? 0
        VStack(spacing: 6) {
            HStack {
                ForEach(stops.indices, id: \.self) { i in
                    Text(stops[i].1).font(.system(size: 11.5)).foregroundStyle(i == index ? TGS.accent : TGS.secondary)
                    if i < stops.count - 1 { Spacer() }
                }
            }
            GeometryReader { geo in
                let w = geo.size.width
                let step = stops.count > 1 ? w / CGFloat(stops.count - 1) : 0
                ZStack(alignment: .leading) {
                    Capsule().fill(Color(hex: 0xE3E3E6)).frame(height: 3)
                    Capsule().fill(TGS.accent).frame(width: step * CGFloat(index), height: 3)
                    ForEach(stops.indices, id: \.self) { i in
                        Circle().fill(i <= index ? TGS.accent : Color(hex: 0xE3E3E6)).frame(width: 7, height: 7)
                            .offset(x: step * CGFloat(i) - 3.5)
                    }
                    Circle().fill(.white).frame(width: 20, height: 20).shadow(color: .black.opacity(0.25), radius: 2, y: 1)
                        .offset(x: step * CGFloat(index) - 10)
                }
                .frame(height: 22)
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0).onChanged { g in
                    guard step > 0 else { return }
                    let i = Int((g.location.x / step).rounded())
                    value = stops[min(max(i, 0), stops.count - 1)].0
                })
            }
            .frame(height: 22)
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
    }
}

func formatBytes(_ b: Double) -> String {
    ByteCountFormatter.string(fromByteCount: Int64(b), countStyle: .file)
}

// MARK: Panes

struct AccountsSettings: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        TGSection(header: "Nền tảng", footer: "Phiên đăng nhập và dữ liệu chỉ lưu cục bộ trong ~/Library/Application Support/ChatSpace.") {
            ForEach(Array(Platform.allCases.enumerated()), id: \.element) { i, p in
                let s = model.session(p)
                HStack(spacing: 12) {
                    PlatformIcon(platform: p, size: 30)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(p.displayName).font(.system(size: 13.5))
                        Text(s.selfInfo?.name ?? stateText(s.authState)).font(.system(size: 11.5)).foregroundStyle(TGS.secondary)
                    }
                    Spacer()
                    if s.authState == .loggedIn { Button("Đăng xuất") { s.logout() }.buttonStyle(.link).foregroundStyle(.red) }
                    Button("Khởi động lại") { s.restart() }.buttonStyle(.link)
                    Button("Log") { NSWorkspace.shared.open(p.dataDirectory.appendingPathComponent("bridge.log")) }.buttonStyle(.link)
                }
                .padding(.horizontal, 14).frame(minHeight: 50)
                .overlay(alignment: .top) { if i > 0 { Rectangle().fill(TGS.separator).frame(height: 1).padding(.leading, 56) } }
            }
        }
    }

    private func stateText(_ s: AuthState) -> String {
        switch s {
        case .loggedIn: "Đã đăng nhập"
        case .connecting: "Đang kết nối…"
        case .awaitingQR: "Chờ quét mã QR"
        case .awaitingCode, .awaitingPassword, .awaitingInput: "Đang đăng nhập"
        case .error: "Lỗi — xem log"
        case .loggedOut: "Chưa đăng nhập"
        }
    }
}

struct NotificationSettings: View {
    @State private var status = "…"

    var body: some View {
        TGSection(header: "Thông báo hệ thống", footer: "Âm báo, nhóm theo cuộc trò chuyện, trả lời nhanh và tự gỡ khi đã đọc — giống app gốc. Chat đã tắt thông báo không hiện banner và không tính vào huy hiệu.") {
            TGRow(icon: "bell.fill", iconColor: Color(hex: 0xFF3B30), title: "Trạng thái", subtitle: status, first: true) {
                Button("Mở Cài đặt hệ thống") {
                    NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=vn.chatspace.app")!)
                }
                .buttonStyle(.link)
            }
        }
        .task { status = await Notifier.shared.statusDescription() }
    }
}

struct StorageSettingsPane: View {
    @Environment(AppModel.self) private var model
    @State private var usage: [Platform: PlatformSession.StorageUsage] = [:]
    @State private var clearing: Platform?

    var body: some View {
        @Bindable var ws = Workspace.shared
        let total = usage.values.reduce(0) { $0 + $1.mediaBytes + $1.databaseBytes }
        TGSection(header: "Bộ nhớ đã dùng", footer: "Ảnh, video và tệp luôn được tải từ cloud khi xem; chỉ khi nền tảng bắt buộc tải về (WhatsApp mã hoá đầu cuối, Telegram) thì mới lưu vào bộ nhớ đệm, sau khi nén.") {
            HStack(spacing: 20) {
                StorageDonut(parts: Platform.allCases.map { (color(for: $0), (usage[$0]?.mediaBytes ?? 0) + (usage[$0]?.databaseBytes ?? 0)) })
                    .frame(width: 120, height: 120)
                    .overlay(VStack(spacing: 0) {
                        Text(formatBytes(total)).font(.system(size: 15, weight: .semibold))
                        Text("tổng").font(.system(size: 11)).foregroundStyle(TGS.secondary)
                    })
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(Platform.allCases) { p in
                        HStack(spacing: 8) {
                            Circle().fill(color(for: p)).frame(width: 9, height: 9)
                            Text(p.displayName).font(.system(size: 12.5))
                            Spacer()
                            if let u = usage[p] {
                                Text("\(formatBytes(u.mediaBytes)) media · \(formatBytes(u.databaseBytes)) dữ liệu")
                                    .font(.system(size: 11.5)).foregroundStyle(TGS.secondary)
                            } else {
                                Text("—").foregroundStyle(TGS.secondary)
                            }
                        }
                    }
                }
            }
            .padding(14)
            ForEach(Platform.allCases.filter { $0 != .messenger }) { p in
                TGRow(icon: "trash.fill", iconColor: color(for: p), title: "Xoá bộ nhớ đệm \(p.displayName)",
                      subtitle: usage[p].map { "\($0.mediaFiles) tệp · \(formatBytes($0.mediaBytes))" }) {
                    if clearing == p { ProgressView().controlSize(.small) } else {
                        Button("Xoá") { clear(p) }.buttonStyle(.link).foregroundStyle(.red)
                            .disabled((usage[p]?.mediaBytes ?? 0) == 0)
                    }
                }
            }
        }
        TGSection(header: "Giữ media", footer: "Ảnh, video và tệp không mở lại trong khoảng thời gian này sẽ bị xoá khỏi máy; chúng vẫn còn trên cloud và được tải lại khi cần.") {
            TickSlider(stops: [(3, "3 ngày"), (7, "1 tuần"), (30, "1 tháng"), (0, "Mãi mãi")], value: $ws.storage.keepMediaDays)
        }
        TGSection(header: "Dung lượng bộ nhớ đệm tối đa", footer: "Khi vượt mức, các tệp lâu không dùng nhất bị xoá trước.") {
            TickSlider(stops: [(1024, "1 GB"), (2048, "2 GB"), (5120, "5 GB"), (20480, "20 GB"), (0, "Không giới hạn")], value: $ws.storage.maxCacheMB)
        }
        TGSection(header: "Tin nhắn lưu cục bộ", footer: "Số tin nhắn mỗi đoạn chat được giữ trên máy (nhóm Zalo, WhatsApp). Tin cũ hơn được tải lại khi cuộn lên. Chat 1-1 của Zalo luôn giữ đủ vì Zalo không cho tải lại lịch sử cũ.") {
            TickSlider(stops: [(200, "200"), (1000, "1.000"), (5000, "5.000"), (0, "Không giới hạn")], value: $ws.storage.maxStoredMessagesPerChat)
        }
        TGSection(header: "Siêu nén", footer: "Ảnh tải về được chuyển sang HEIC chất lượng 60%, cạnh dài tối đa 1600 px — thường nhỏ hơn 5–10 lần mà vẫn nét trên màn hình.") {
            TGRow(icon: "arrow.down.right.and.arrow.up.left", iconColor: Color(hex: 0x34C759), title: "Nén ảnh khi tải về", first: true) {
                Toggle("", isOn: $ws.storage.compressImages).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
        }
        TGSection(header: "Tự động tải media", footer: "Media không tự tải sẽ hiện ảnh xem trước; bấm vào để tải.") {
            TGRow(icon: "photo.fill", iconColor: Color(hex: 0x3390EC), title: "Ảnh", first: true) {
                Toggle("", isOn: $ws.storage.autoDownloadPhotos).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            TGRow(icon: "video.fill", iconColor: Color(hex: 0xFF9500), title: "Video") {
                Toggle("", isOn: $ws.storage.autoDownloadVideos).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            TGRow(icon: "doc.fill", iconColor: Color(hex: 0x5E5CE6), title: "Tệp") {
                Toggle("", isOn: $ws.storage.autoDownloadFiles).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            TickSlider(stops: [(1.0, "1 MB"), (5.0, "5 MB"), (10.0, "10 MB"), (50.0, "50 MB"), (100.0, "100 MB")], value: $ws.storage.autoDownloadMaxMB)
        }
        .task { await reload() }
    }

    private func color(for p: Platform) -> Color {
        switch p {
        case .zalo: Color(hex: 0x0068FF)
        case .messenger: Color(hex: 0xA033FF)
        case .whatsapp: Color(hex: 0x25D366)
        case .telegram: Color(hex: 0x2AABEE)
        }
    }

    private func reload() async {
        for p in Platform.allCases {
            if let u = await model.session(p).storageUsage() { usage[p] = u }
        }
    }

    private func clear(_ p: Platform) {
        clearing = p
        Task {
            _ = await model.session(p).clearStorage()
            await reload()
            clearing = nil
        }
    }
}

/// Telegram's storage ring.
struct StorageDonut: View {
    let parts: [(Color, Double)]

    var body: some View {
        let total = max(parts.reduce(0) { $0 + $1.1 }, 1)
        ZStack {
            Circle().stroke(Color(hex: 0xE3E3E6), lineWidth: 14)
            ForEach(parts.indices, id: \.self) { i in
                let start = parts[..<i].reduce(0) { $0 + $1.1 } / total
                let end = start + parts[i].1 / total
                if end > start {
                    Circle().trim(from: start + 0.004, to: max(start + 0.004, end - 0.004))
                        .stroke(parts[i].0, style: StrokeStyle(lineWidth: 14, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                }
            }
        }
        .padding(7)
    }
}

struct GroupsSettings: View {
    @Environment(AppModel.self) private var model
    @State private var editing: CustomGroup?

    var body: some View {
        @Bindable var ws = Workspace.shared
        TGSection(header: "ChatSpace", footer: "Mục “Chưa đọc” trên thanh bên gom tin chưa đọc của mọi nền tảng.") {
            TGRow(icon: "tray.full.fill", iconColor: Color(hex: 0x5E5CE6), title: "Hiện mục Chưa đọc", first: true) {
                Toggle("", isOn: $ws.showInbox).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
        }
        TGSection(header: "Nhóm tuỳ chỉnh", footer: "Mỗi nhóm là một mục trên thanh bên, gom mọi đoạn chat có tên chứa từ khoá (VD “VF” gom “VF 1”, “VF-2”, “VF3”…).") {
            ForEach(Array(ws.groups.enumerated()), id: \.element.id) { i, g in
                HStack(spacing: 12) {
                    GroupIcon(group: g, size: 28)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(g.name).font(.system(size: 13.5))
                        Text("\(g.keywords.joined(separator: ", ")) · \(model.groupRefs(g).count) đoạn chat")
                            .font(.system(size: 11.5)).foregroundStyle(TGS.secondary)
                    }
                    Spacer()
                    Button("Sửa") { editing = g }.buttonStyle(.link)
                    Button("Xoá") { ws.delete(g.id) }.buttonStyle(.link).foregroundStyle(.red)
                }
                .padding(.horizontal, 14).frame(minHeight: 46)
                .overlay(alignment: .top) { if i > 0 { Rectangle().fill(TGS.separator).frame(height: 1).padding(.leading, 54) } }
            }
            Button { editing = CustomGroup(name: "", keywords: []) } label: {
                TGRow(icon: "plus", iconColor: TGS.accent, title: "Tạo nhóm mới", first: ws.groups.isEmpty, titleColor: TGS.accent)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
        .sheet(item: $editing) { GroupEditor(group: $0).environment(model) }
    }
}

struct IgnoredSettings: View {
    var body: some View {
        let ws = Workspace.shared
        TGSection(header: "Đoạn chat đã bỏ qua", footer: "Các đoạn chat này không hiện trong mục Chưa đọc của ChatSpace và không tính vào huy hiệu của nó. Chúng vẫn hiện bình thường trong giao diện của từng nền tảng.") {
            if ws.ignored.isEmpty {
                TGRow(title: "Chưa bỏ qua đoạn chat nào", subtitle: "Chuột phải vào một đoạn chat → “Bỏ qua cho lần sau”.", first: true)
            }
            ForEach(Array(ws.ignored.sorted { $0.ignoredAt > $1.ignoredAt }.enumerated()), id: \.element.id) { i, item in
                HStack(spacing: 12) {
                    PlatformIcon(platform: item.ref.platform, size: 26)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(item.title).font(.system(size: 13.5)).lineLimit(1)
                        Text("\(item.ref.platform.displayName) · bỏ qua \(item.ignoredAt.formatted(.relative(presentation: .named).locale(Locale(identifier: "vi_VN"))))")
                            .font(.system(size: 11.5)).foregroundStyle(TGS.secondary)
                    }
                    Spacer()
                    Button("Khôi phục") { ws.restore(item.ref) }.buttonStyle(.link)
                }
                .padding(.horizontal, 14).frame(minHeight: 46)
                .overlay(alignment: .top) { if i > 0 { Rectangle().fill(TGS.separator).frame(height: 1).padding(.leading, 52) } }
            }
            if ws.ignored.count > 1 {
                Button { ws.ignored = [] } label: {
                    TGRow(icon: "arrow.uturn.backward", iconColor: Color(hex: 0xFF9500), title: "Khôi phục tất cả", titleColor: TGS.accent)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }
}

struct BubbleSettingsPane: View {
    var body: some View {
        @Bindable var ws = Workspace.shared
        TGSection(header: "Bong bóng chat", footer: "Giống Telegram trên Android: mỗi tin nhắn mới làm bong bóng của đoạn chat bật lên ở mép màn hình, trên mọi ứng dụng, kèm nội dung tin nhắn. Bấm để mở khung chat nhỏ, kéo để di chuyển, kéo xuống dấu ✕ để đóng.") {
            TGRow(icon: "bubble.left.and.bubble.right.fill", iconColor: Color(hex: 0x32ADE6), title: "Hiện bong bóng", first: true) {
                Picker("", selection: $ws.bubbles.trigger) {
                    Text("Khi có tin nhắn mới").tag(BubbleSettings.Trigger.newMessages)
                    Text("Chỉ khi tôi mở").tag(BubbleSettings.Trigger.manual)
                    Text("Tắt").tag(BubbleSettings.Trigger.off)
                }
                .labelsHidden().frame(width: 180)
            }
            TGRow(icon: "text.bubble.fill", iconColor: Color(hex: 0xFF9500), title: "Hiện nội dung tin nhắn cạnh bong bóng") {
                Toggle("", isOn: $ws.bubbles.showPreview).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            TGRow(icon: "macwindow", iconColor: Color(hex: 0x8E8E93), title: "Chỉ khi ChatSpace ở chế độ nền") {
                Toggle("", isOn: $ws.bubbles.onlyInBackground).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            TGRow(icon: "person.3.fill", iconColor: Color(hex: 0x34C759), title: "Cả tin nhắn nhóm") {
                Toggle("", isOn: $ws.bubbles.includeGroups).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
            TGRow(icon: "square.stack.fill", iconColor: Color(hex: 0x5E5CE6), title: "Số bong bóng tối đa") {
                Stepper("\(ws.bubbles.maxBubbles)", value: $ws.bubbles.maxBubbles, in: 1...8)
            }
        }
        TGSection(footer: "Chuột phải vào bất kỳ đoạn chat nào → “Mở bong bóng chat” để mở thủ công.") {
            Button {
                let m = AppModel.shared
                if let ref = m.inboxRefs.first ?? Platform.allCases.lazy.compactMap({ p in m.session(p).chats.first.map { ChatRef(platform: p, chatId: $0.id) } }).first {
                    ChatHeads.shared.show(ref, expand: false)
                }
            } label: {
                TGRow(icon: "sparkles", iconColor: Color(hex: 0xFF9500), title: "Thử bong bóng với đoạn chat gần nhất", first: true, titleColor: TGS.accent)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
    }
}

struct TranslationSettingsPane: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        @Bindable var ws = Workspace.shared
        let perChat = ws.translation.chatLanguages.sorted { $0.key < $1.key }
        TGSection(header: "Ngôn ngữ", footer: "Nút dịch nằm cạnh nút bày tỏ cảm xúc khi rê chuột vào tin nhắn; bản dịch hiện in nghiêng ngay trong tin nhắn. Tin bạn gửi giữ nguyên ngôn ngữ này, trừ khi chọn ngôn ngữ khác ở nút 文A trong khung soạn tin.") {
            TGRow(icon: "translate", iconColor: Color(hex: 0x30B0C7), title: "Ngôn ngữ của bạn", first: true) {
                Picker("", selection: $ws.translation.language) {
                    ForEach(TranslationLanguages.all, id: \.code) { Text($0.name).tag($0.code) }
                }
                .labelsHidden().frame(width: 210)
            }
            TGRow(icon: "arrowshape.turn.up.left.fill", iconColor: Color(hex: 0xFF9500), title: "Trả lời bằng ngôn ngữ của tin được trả lời",
                  subtitle: "VD trả lời một tin tiếng Trung thì tự dịch sang tiếng Trung rồi mới gửi") {
                Toggle("", isOn: $ws.translation.matchReplyLanguage).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
        }
        TGSection(header: "Ngôn ngữ gửi theo đoạn chat", footer: "Chọn ở nút 文A trong khung soạn tin của từng đoạn chat.") {
            if perChat.isEmpty {
                TGRow(title: "Mọi đoạn chat gửi nguyên văn", first: true)
            }
            ForEach(Array(perChat.enumerated()), id: \.element.key) { i, item in
                let ref = chatRef(item.key)
                HStack(spacing: 12) {
                    if let ref { PlatformIcon(platform: ref.platform, size: 26) }
                    VStack(alignment: .leading, spacing: 1) {
                        Text(title(ref)).font(.system(size: 13.5)).lineLimit(1)
                        Text("Gửi bằng \(TranslationLanguages.name(item.value))").font(.system(size: 11.5)).foregroundStyle(TGS.secondary)
                    }
                    Spacer()
                    Button("Gửi nguyên văn") { ws.translation.chatLanguages[item.key] = nil }.buttonStyle(.link)
                }
                .padding(.horizontal, 14).frame(minHeight: 46)
                .overlay(alignment: .top) { if i > 0 { Rectangle().fill(TGS.separator).frame(height: 1).padding(.leading, 52) } }
            }
        }
        TGSection(header: "Dịch vụ", footer: "Dùng Google Dịch qua địa chỉ công khai translate.googleapis.com, không cần API key. Nội dung tin được dịch sẽ được gửi tới Google. Bản dịch lưu trên máy để không phải dịch lại.") {
            TGRow(icon: "internaldrive.fill", iconColor: Color(hex: 0x8E8E93), title: "Bản dịch đã lưu",
                  subtitle: "\(Translator.shared.entries.count) tin nhắn", first: true) {
                Button("Xoá") { Translator.shared.clearCache() }.buttonStyle(.link).foregroundStyle(.red)
                    .disabled(Translator.shared.entries.isEmpty)
            }
        }
    }

    private func chatRef(_ id: String) -> ChatRef? {
        let parts = id.split(separator: "|", maxSplits: 1).map(String.init)
        guard parts.count == 2, let p = Platform(rawValue: parts[0]) else { return nil }
        return ChatRef(platform: p, chatId: parts[1])
    }

    private func title(_ ref: ChatRef?) -> String {
        guard let ref else { return "Đoạn chat" }
        return model.session(ref.platform).chats.first { $0.id == ref.chatId }?.title ?? ref.chatId
    }
}

struct ComposerSettingsPane: View {
    var body: some View {
        @Bindable var ws = Workspace.shared
        TGSection(header: "Phép tính", footer: "VD gõ \"Số tiền: 23500*410\" rồi Enter: trên khung soạn tin hiện bản \"23500*410 = 9.635.000\" để chọn gửi bản có kết quả (Enter lần nữa) hoặc bản gốc. Ngày tháng, số điện thoại, khoảng \"8h - 17h\" và phép tính đã có dấu = không bị nhận nhầm.") {
            TGRow(icon: "function", iconColor: Color(hex: 0xAF52DE), title: "Nhận diện phép tính trước khi gửi",
                  subtitle: "Cộng, trừ, nhân (* x ×), chia, luỹ thừa ^, phần trăm %", first: true) {
                Toggle("", isOn: $ws.mathPreview).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
        }
    }
}

struct TelegramAPISettings: View {
    @Environment(AppModel.self) private var model
    @State private var apiId = AppSettings.telegramApiId
    @State private var apiHash = AppSettings.telegramApiHash

    var body: some View {
        TGSection(header: "Ứng dụng của bạn", footer: "Lấy tại my.telegram.org → API development tools. Lưu cục bộ trên máy này.") {
            TGRow(title: "API ID", first: true) {
                TextField("", text: $apiId).textFieldStyle(.plain).multilineTextAlignment(.trailing).frame(width: 260)
            }
            TGRow(title: "API Hash") {
                SecureField("", text: $apiHash).textFieldStyle(.plain).multilineTextAlignment(.trailing).frame(width: 260)
            }
            Button {
                AppSettings.telegramApiId = apiId.trimmingCharacters(in: .whitespaces)
                AppSettings.telegramApiHash = apiHash.trimmingCharacters(in: .whitespaces)
                model.session(.telegram).restart()
            } label: {
                TGRow(title: "Lưu & khởi động lại Telegram", titleColor: TGS.accent).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
    }
}

struct AssetsSettings: View {
    var body: some View {
        TGSection(header: "App gốc trên máy", footer: "ChatSpace đọc tài nguyên trực tiếp từ các app chính thức đã cài trên máy này, không sao chép hay phân phối lại.") {
            ForEach(Array(Platform.allCases.enumerated()), id: \.element) { i, p in
                HStack(spacing: 12) {
                    PlatformIcon(platform: p, size: 26)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(p.displayName).font(.system(size: 13.5))
                        Text(NativeAssets.shared.appURL(p).map { "Dùng icon, âm thanh, hình ảnh từ \($0.lastPathComponent)" } ?? "Chưa cài app gốc — dùng bản vẽ lại")
                            .font(.system(size: 11.5)).foregroundStyle(TGS.secondary)
                    }
                    Spacer()
                }
                .padding(.horizontal, 14).frame(minHeight: 46)
                .overlay(alignment: .top) { if i > 0 { Rectangle().fill(TGS.separator).frame(height: 1).padding(.leading, 52) } }
            }
        }
    }
}

struct UpdateSettings: View {
    var body: some View {
        @Bindable var u = Updater.shared
        TGSection(header: "Phiên bản", footer: u.supported ? "Bản mới được tải ngầm từ GitHub (\(Updater.repo)) và cài khi bạn bấm Khởi động lại hoặc khi thoát ChatSpace." : "Bản build này (dev / demo / chạy ngoài .app) không tự cập nhật.") {
            TGRow(title: "ChatSpace \(u.currentVersion)", subtitle: status(u), first: true) {
                switch u.state {
                case .ready:
                    Button("Khởi động lại để cập nhật") { u.restartToUpdate() }.buttonStyle(.borderedProminent).controlSize(.small)
                case .checking, .downloading:
                    ProgressView().controlSize(.small)
                default:
                    Button("Kiểm tra ngay") { Task { await u.check() } }.buttonStyle(.link).disabled(!u.supported)
                }
            }
            TGRow(title: "Tự động kiểm tra và tải bản mới") {
                Toggle("", isOn: $u.automatic).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }
        }
    }

    private func status(_ u: Updater) -> String {
        switch u.state {
        case .idle: "Chưa kiểm tra"
        case .checking: "Đang kiểm tra…"
        case .upToDate: "Đang dùng bản mới nhất" + (u.lastCheck.map { " · kiểm tra lúc \($0.formatted(date: .omitted, time: .shortened))" } ?? "")
        case .downloading(let v): "Đang tải bản \(v)…"
        case .ready(let v): "Đã tải bản \(v), sẵn sàng cài đặt"
        case .failed(let e): "Lỗi: \(e)"
        }
    }
}
