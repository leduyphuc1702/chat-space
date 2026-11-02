import SwiftUI
import UniformTypeIdentifiers

/// Create or edit a custom group ("nền tảng tuỳ chỉnh").
struct GroupEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State var group: CustomGroup
    @State private var keywordText: String

    init(group: CustomGroup) {
        _group = State(initialValue: group)
        _keywordText = State(initialValue: group.keywords.joined(separator: ", "))
    }

    static let symbols = [
        "folder.fill", "star.fill", "heart.fill", "briefcase.fill", "building.2.fill", "house.fill",
        "car.fill", "bolt.fill", "cart.fill", "bag.fill", "creditcard.fill", "dollarsign.circle.fill",
        "chart.bar.fill", "person.2.fill", "person.3.fill", "graduationcap.fill", "book.fill", "gamecontroller.fill",
        "airplane", "globe.asia.australia.fill", "flag.fill", "bell.fill", "megaphone.fill", "tag.fill",
        "shippingbox.fill", "wrench.and.screwdriver.fill", "hammer.fill", "leaf.fill", "flame.fill", "sparkles",
        "cup.and.saucer.fill", "fork.knife", "music.note", "camera.fill", "paintpalette.fill", "stethoscope",
        "bitcoinsign.circle.fill", "lock.fill", "shield.fill", "doc.text.fill", "envelope.fill", "message.fill",
    ]
    static let colors: [UInt32] = [0x5E5CE6, 0x0A84FF, 0x32ADE6, 0x30D158, 0xFFD60A, 0xFF9F0A, 0xFF453A, 0xFF375F, 0xBF5AF2, 0x8E8E93]

    private var keywords: [String] {
        keywordText.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    private var draft: CustomGroup {
        var g = group
        g.keywords = keywords
        if g.name.trimmingCharacters(in: .whitespaces).isEmpty { g.name = keywords.first ?? "" }
        return g
    }

    var body: some View {
        let isNew = Workspace.shared.group(group.id) == nil
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                GroupIcon(group: draft, size: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text(isNew ? "Nhóm tuỳ chỉnh mới" : "Sửa nhóm").font(.system(size: 17, weight: .bold))
                    Text("Gom mọi đoạn chat có tên khớp từ khoá, trên tất cả nền tảng, vào một mục riêng ở thanh bên.")
                        .font(.system(size: 12)).foregroundStyle(.secondary)
                }
            }
            .padding(20)

            Form {
                Section {
                    TextField("Tên", text: $group.name, prompt: Text(keywords.first ?? "VD: Dự án VF"))
                    TextField("Từ khoá", text: $keywordText, prompt: Text("VF, VinFast"))
                    Picker("Cách so khớp", selection: $group.match) {
                        Text("Chứa từ khoá").tag(CustomGroup.Match.contains)
                        Text("Bắt đầu bằng từ khoá").tag(CustomGroup.Match.prefix)
                        Text("Từ khoá đứng đầu một từ").tag(CustomGroup.Match.word)
                    }
                } footer: {
                    Text("Phân cách nhiều từ khoá bằng dấu phẩy. Không phân biệt hoa thường và dấu: “VF” khớp “VF 1”, “VF-2”, “vf3”.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                Section("Nền tảng") {
                    HStack(spacing: 14) {
                        ForEach(Platform.allCases) { p in
                            Toggle(isOn: Binding(
                                get: { group.platforms.isEmpty || group.platforms.contains(p) },
                                set: { on in
                                    var set = group.platforms.isEmpty ? Platform.allCases : group.platforms
                                    if on { if !set.contains(p) { set.append(p) } } else { set.removeAll { $0 == p } }
                                    group.platforms = set.count == Platform.allCases.count ? [] : set
                                })) {
                                HStack(spacing: 5) { PlatformIcon(platform: p, size: 16); Text(p.displayName) }
                            }
                            .toggleStyle(.checkbox)
                        }
                    }
                }
                Section("Biểu tượng") {
                    LazyVGrid(columns: Array(repeating: GridItem(.fixed(30), spacing: 6), count: 14), spacing: 6) {
                        ForEach(Self.symbols, id: \.self) { sym in
                            let on = group.imagePath == nil && group.symbol == sym
                            Button { group.symbol = sym; group.imagePath = nil } label: {
                                Image(systemName: sym).font(.system(size: 14))
                                    .frame(width: 30, height: 30)
                                    .foregroundStyle(on ? .white : .primary)
                                    .background(RoundedRectangle(cornerRadius: 7).fill(on ? Color(hex: group.colorHex) : Color.primary.opacity(0.06)))
                                    .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    HStack(spacing: 8) {
                        ForEach(Self.colors, id: \.self) { c in
                            Button { group.colorHex = c } label: {
                                Circle().fill(Color(hex: c)).frame(width: 20, height: 20)
                                    .overlay(Circle().stroke(Color.primary.opacity(group.colorHex == c ? 0.8 : 0), lineWidth: 2).padding(-3))
                                    .contentShape(Circle())
                            }
                            .buttonStyle(.plain)
                        }
                        Spacer()
                        Button("Chọn ảnh…") { pickImage() }
                        if group.imagePath != nil { Button("Bỏ ảnh") { group.imagePath = nil } }
                    }
                }
                Section("Xem trước — \(matches.count) đoạn chat khớp") {
                    if matches.isEmpty {
                        Text(keywords.isEmpty ? "Nhập từ khoá để xem các đoạn chat sẽ được gom." : "Chưa có đoạn chat nào khớp.")
                            .foregroundStyle(.secondary)
                    } else {
                        ForEach(matches.prefix(8)) { ref in
                            HStack(spacing: 8) {
                                PlatformIcon(platform: ref.platform, size: 16)
                                Text(AppModel.shared.chat(ref)?.title ?? ref.chatId).lineLimit(1)
                            }
                        }
                        if matches.count > 8 { Text("và \(matches.count - 8) đoạn chat khác…").foregroundStyle(.secondary) }
                    }
                }
            }
            .formStyle(.grouped)

            HStack {
                if !isNew {
                    Button("Xoá nhóm", role: .destructive) {
                        Workspace.shared.delete(group.id)
                        dismiss()
                    }
                }
                Spacer()
                Button("Huỷ") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(isNew ? "Tạo nhóm" : "Lưu") {
                    Workspace.shared.upsert(draft)
                    AppModel.shared.rail = .group(group.id)
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
                .disabled(keywords.isEmpty)
            }
            .padding(16)
        }
        .frame(width: 600, height: 700)
    }

    private var matches: [ChatRef] { keywords.isEmpty ? [] : AppModel.shared.groupRefs(draft) }

    private func pickImage() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url,
              let path = Workspace.importGroupImage(url, for: group.id) else { return }
        group.imagePath = path
    }
}
