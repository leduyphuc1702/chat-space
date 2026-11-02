import SwiftUI

/// Shown above a composer when the message being sent contains arithmetic: send it with the results or as typed.
struct MathSendPreview: View {
    let session: PlatformSession
    let chatId: String
    var accent: Color

    var body: some View {
        let draft = (session.drafts[chatId] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        // Editing the draft makes the preview stale; the next send builds a new one.
        if let p = session.mathPreviews[chatId], p.original == draft {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 6) {
                    Image(systemName: "function").foregroundStyle(accent)
                    Text("Tin nhắn có phép tính").font(.system(size: 12, weight: .semibold))
                    Spacer(minLength: 4)
                    Button { session.mathPreviews[chatId] = nil } label: { Image(systemName: "xmark").foregroundStyle(.secondary) }
                        .buttonStyle(.plain)
                        .keyboardShortcut(.cancelAction)
                        .help("Huỷ gửi để sửa tiếp (Esc)")
                }
                ScrollView {
                    Text(rendered(p.pieces))
                        .font(.system(size: 13))
                        .foregroundStyle(Color(hex: 0x333333))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxHeight: 132)
                .fixedSize(horizontal: false, vertical: true)
                .padding(8)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.white))
                HStack(spacing: 8) {
                    Button { session.resolveMath(in: chatId, computed: true) } label: {
                        Text("Gửi bản có kết quả  ↩").font(.system(size: 12.5, weight: .semibold)).foregroundStyle(.white)
                            .padding(.horizontal, 12).frame(height: 28)
                            .background(RoundedRectangle(cornerRadius: 6).fill(accent))
                    }
                    .buttonStyle(.plain)
                    .help("Gửi kèm kết quả. Nhấn Enter lần nữa trong khung soạn tin cũng gửi bản này.")
                    Button { session.resolveMath(in: chatId, computed: false) } label: {
                        Text("Gửi bản gốc").font(.system(size: 12.5)).foregroundStyle(Color(hex: 0x333333))
                            .padding(.horizontal, 12).frame(height: 28)
                            .background(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.black.opacity(0.15)))
                    }
                    .buttonStyle(.plain)
                    .help("Gửi đúng như đã gõ, không thêm kết quả")
                    Spacer(minLength: 0)
                }
            }
            .padding(10)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color(hex: 0xF2F4F7)))
            .padding(.horizontal, 12).padding(.top, 8)
        }
    }

    private func rendered(_ pieces: [MessageMath.Piece]) -> AttributedString {
        pieces.reduce(into: AttributedString()) { out, piece in
            var s = AttributedString(piece.text)
            if piece.isResult {
                s.font = .system(size: 13, weight: .bold)
                s.foregroundColor = accent
            }
            out += s
        }
    }
}
