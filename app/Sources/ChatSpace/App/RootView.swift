import SwiftUI

/// Left rail + whatever it selects: the ChatSpace inbox, one platform's own full UI, or a custom group.
/// Only the selected view is mounted: hidden AppKit views (text views, scroll views) would otherwise
/// keep receiving clicks and stealing focus. Selection, drafts and replies live in the sessions.
struct RootView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        HStack(spacing: 0) {
            PlatformRail()
            Group {
                switch model.rail {
                case .inbox: AggregateView(source: .inbox)
                case .platform(let p): platformView(p)
                case .group(let id): AggregateView(source: .group(id))
                }
            }
            .id(model.rail.key)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .ignoresSafeArea()
        .background(Color(nsColor: .windowBackgroundColor))
        .overlay {
            if model.searchOpen { GlobalSearchOverlay() }
        }
    }

    @ViewBuilder
    private func platformView(_ p: Platform) -> some View {
        let s = model.session(p)
        switch p {
        case .zalo: ZaloRootView(session: s)
        case .messenger: MessengerRootView(session: s)
        case .whatsapp: WhatsAppRootView(session: s)
        case .telegram: TelegramRootView(session: s)
        }
    }
}

/// Thin vertical switcher (like a dock): ChatSpace inbox, each platform's app icon, custom groups.
struct PlatformRail: View {
    @Environment(AppModel.self) private var model
    @State private var editing: CustomGroup?
    private static let width: CGFloat = 68

    var body: some View {
        let ws = Workspace.shared
        VStack(spacing: 0) {
            Color.clear.frame(height: 30) // traffic lights
            Button { model.searchOpen.toggle() } label: {
                Image(systemName: "magnifyingglass").font(.system(size: 16, weight: .semibold))
                    .foregroundStyle(.white.opacity(model.searchOpen ? 1 : 0.75))
                    .frame(width: 44, height: 32)
                    .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(.white.opacity(model.searchOpen ? 0.18 : 0.08)))
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .focusEffectDisabled()
            .help("Tìm kiếm trên mọi nền tảng — tin nhắn, tệp, chữ trong ảnh (⌘K)")
            .padding(.bottom, 10)
            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: 14) {
                    if ws.showInbox {
                        item(.inbox, help: "ChatSpace — tin chưa đọc (⌘0)", badge: model.inboxBadge) {
                            ChatSpaceIcon(size: 38)
                        }
                        divider
                    }
                    ForEach(Platform.allCases) { p in
                        let s = model.session(p)
                        item(.platform(p), help: p.displayName, badge: s.unreadTotal,
                             warn: s.authState != .loggedIn && s.authState != .connecting) {
                            PlatformIcon(platform: p, size: 38)
                        }
                    }
                    divider
                    ForEach(ws.groups) { g in
                        item(.group(g.id), help: "\(g.name) — \(g.keywords.joined(separator: ", "))", badge: model.groupBadge(g)) {
                            GroupIcon(group: g, size: 38)
                        }
                        .contextMenu {
                            Button("Sửa nhóm…") { editing = g }
                            Button("Xoá nhóm", role: .destructive) { ws.delete(g.id) }
                        }
                    }
                    Button { editing = CustomGroup(name: "", keywords: []) } label: {
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .strokeBorder(.white.opacity(0.28), style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                            .frame(width: 38, height: 38)
                            .overlay(Image(systemName: "plus").font(.system(size: 16, weight: .semibold)).foregroundStyle(.white.opacity(0.7)))
                            .padding(5)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .focusEffectDisabled()
                    .help("Tạo nhóm tuỳ chỉnh: gom các đoạn chat có tên chứa từ khoá")
                }
                // Full rail width: the scroll view clips to its content, which cut off badges and the selection bar.
                .frame(width: Self.width)
                .padding(.vertical, 4)
            }
            .scrollIndicators(.never)
            if let v = Updater.shared.readyVersion {
                Button { Updater.shared.restartToUpdate() } label: {
                    Image(systemName: "arrow.down.circle.fill").font(.system(size: 22)).foregroundStyle(Color(hex: 0x34C759))
                        .frame(width: 44, height: 36).contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .focusEffectDisabled()
                .help("Đã tải ChatSpace \(v) — bấm để khởi động lại và cập nhật (hoặc tự cập nhật khi thoát app)")
                .padding(.top, 8)
            }
            SettingsLink {
                Image(systemName: "gearshape").font(.system(size: 17)).foregroundStyle(.white.opacity(0.7))
                    .frame(width: 44, height: 36).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .focusEffectDisabled()
            .padding(.vertical, 12)
        }
        .frame(width: Self.width)
        .background(Color(hex: 0x1C1C1E))
        .sheet(item: $editing) { g in GroupEditor(group: g) }
    }

    private var divider: some View {
        Rectangle().fill(.white.opacity(0.14)).frame(width: 30, height: 1)
    }

    private func item<Icon: View>(_ r: RailItem, help: String, badge: Int, warn: Bool = false,
                                  @ViewBuilder icon: () -> Icon) -> some View {
        let selected = model.rail == r
        return Button { model.rail = r } label: {
            icon()
                .padding(5)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(selected ? Color.white.opacity(0.14) : .clear))
                .overlay(alignment: .leading) {
                    if selected { Capsule().fill(.white).frame(width: 3, height: 22).offset(x: -10) }
                }
                .overlay(alignment: .topTrailing) {
                    if badge > 0 {
                        RailBadge(count: badge).offset(x: 4, y: -3)
                    } else if warn {
                        Circle().fill(Color.orange).frame(width: 9, height: 9).offset(x: 1, y: 1)
                    }
                }
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .focusEffectDisabled()
        .help(help)
    }
}

struct RailBadge: View {
    let count: Int
    var body: some View {
        Text(count > 99 ? "99+" : "\(count)")
            .font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
            .padding(.horizontal, 5).frame(minWidth: 17, minHeight: 17)
            .background(Capsule().fill(Color(hex: 0xFF3B30)))
    }
}

/// ChatSpace's own icon (bundle icon, or the dev fallback drawn from the same colours).
struct ChatSpaceIcon: View {
    var size: CGFloat
    private static let image: NSImage? = Bundle.main.image(forResource: "AppIcon")
        ?? Platform.repoRoot().flatMap { NSImage(contentsOf: $0.appendingPathComponent("app/Resources/AppIcon.icns")) }

    var body: some View {
        if let img = Self.image {
            Image(nsImage: img).resizable().interpolation(.high)
                .frame(width: size * 1.22, height: size * 1.22).frame(width: size, height: size)
        } else {
            RoundedRectangle(cornerRadius: size * 0.23, style: .continuous)
                .fill(LinearGradient(colors: [Color(hex: 0x6E5BFF), Color(hex: 0x2EA8FF)], startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay(Image(systemName: "bubble.left.and.bubble.right.fill").font(.system(size: size * 0.42)).foregroundStyle(.white))
                .frame(width: size, height: size)
        }
    }
}

/// A custom group's icon: the picked image, or an SF Symbol on the group's colour.
struct GroupIcon: View {
    let group: CustomGroup
    var size: CGFloat

    var body: some View {
        if let p = group.imagePath, let img = NSImage(contentsOfFile: p) {
            Image(nsImage: img).resizable().scaledToFill()
                .frame(width: size, height: size)
                .clipShape(RoundedRectangle(cornerRadius: size * 0.23, style: .continuous))
        } else {
            RoundedRectangle(cornerRadius: size * 0.23, style: .continuous)
                .fill(LinearGradient(colors: [Color(hex: group.colorHex).opacity(0.85), Color(hex: group.colorHex)], startPoint: .top, endPoint: .bottom))
                .overlay(Image(systemName: group.symbol ?? "folder.fill").font(.system(size: size * 0.46, weight: .medium)).foregroundStyle(.white))
                .frame(width: size, height: size)
        }
    }
}

/// The official app's own icon when it is installed, otherwise a drawn approximation.
struct PlatformIcon: View {
    let platform: Platform
    var size: CGFloat

    var body: some View {
        if let icon = NativeAssets.shared.appIcon(platform) {
            // macOS icons carry ~10% transparent margin; enlarge so the tile fills `size`.
            Image(nsImage: icon).resizable().interpolation(.high)
                .frame(width: size * 1.22, height: size * 1.22)
                .frame(width: size, height: size)
        } else {
            drawn
        }
    }

    @ViewBuilder private var drawn: some View {
        let r = size * 0.23
        switch platform {
        case .zalo:
            RoundedRectangle(cornerRadius: r, style: .continuous)
                .fill(Color(hex: 0x0068FF))
                .overlay(
                    Text("Zalo").font(.system(size: size * 0.3, weight: .heavy)).foregroundStyle(.white)
                        .padding(.horizontal, size * 0.1).padding(.vertical, size * 0.06)
                        .background(Capsule().stroke(.white, lineWidth: size * 0.045)))
                .frame(width: size, height: size)
        case .messenger:
            RoundedRectangle(cornerRadius: r, style: .continuous)
                .fill(.white)
                .overlay(MessengerLogo(size: size * 0.7))
                .frame(width: size, height: size)
        case .whatsapp:
            RoundedRectangle(cornerRadius: r, style: .continuous)
                .fill(Color(hex: 0x25D366))
                .overlay(
                    Image(systemName: "phone.bubble.fill").resizable().scaledToFit()
                        .frame(width: size * 0.62).foregroundStyle(.white))
                .frame(width: size, height: size)
        case .telegram:
            RoundedRectangle(cornerRadius: r, style: .continuous)
                .fill(LinearGradient(colors: [Color(hex: 0x37BBFE), Color(hex: 0x007DBB)], startPoint: .top, endPoint: .bottom))
                .overlay(
                    Image(systemName: "paperplane.fill").resizable().scaledToFit()
                        .frame(width: size * 0.5).foregroundStyle(.white).offset(x: -size * 0.03, y: size * 0.02))
                .frame(width: size, height: size)
        }
    }
}
