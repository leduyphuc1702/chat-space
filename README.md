# ChatSpace

Ứng dụng macOS native (SwiftUI + AppKit, **không WebView**) gom Zalo, Messenger, WhatsApp và Telegram vào một cửa sổ. Mỗi nền tảng có giao diện mô phỏng sát app gốc (Zalo PC, Messenger, WhatsApp Desktop, Telegram for macOS) thay vì một giao diện chung.

## Kiến trúc

```
ChatSpace.app (Swift)
 ├─ PlatformRail ─ chuyển giữa 4 giao diện riêng (⌘1…⌘4)
 ├─ PlatformSession × 4 ─ trạng thái quan sát được, nối với bridge qua stdio
 └─ Contents/Resources/bridges/
     ├─ chatspace-bridge     (Bun, 1 binary) --platform zalo | whatsapp | telegram
     │     zca-js · Baileys · GramJS (MTProto)
     └─ chatspace-messenger  (Go) mautrix-meta/messagix (+ whatsmeow cho E2EE)
```

- Mỗi nền tảng chạy trong **một tiến trình riêng**: một bridge lỗi hay bị khoá không kéo theo các bridge khác. App tự khởi động lại bridge khi nó thoát (tối đa 5 lần, lùi thời gian dần).
- Giao thức: JSON theo dòng qua stdin/stdout — xem [docs/PROTOCOL.md](docs/PROTOCOL.md).
- Dữ liệu và phiên đăng nhập lưu cục bộ tại `~/Library/Application Support/ChatSpace/<platform>/`. Log của từng bridge ở `bridge.log` cùng thư mục.

## Build

Yêu cầu: macOS 14+, Swift 5.10+ (Command Line Tools là đủ, không cần Xcode), [Bun](https://bun.sh), Go 1.25+.

```bash
scripts/build-app.sh          # → dist/ChatSpace.app (đã ký ad-hoc)
open dist/ChatSpace.app
```

Chạy khi phát triển (bridge chạy thẳng từ mã TypeScript bằng Bun):

```bash
cd app && swift build
CHATSPACE_DEV=1 .build/debug/ChatSpace
CHATSPACE_DEMO=1 .build/debug/ChatSpace   # dữ liệu mẫu, không cần tài khoản
```

Biến môi trường cho chế độ demo: `CHATSPACE_PLATFORM=zalo|messenger|whatsapp|telegram|inbox|group:<UUID>` (mở sẵn nền tảng, mục Chưa đọc, hoặc nhóm tuỳ chỉnh — demo có sẵn nhóm "VF" `group:00000000-0000-0000-0000-0000000000F1`), `CHATSPACE_DEMO_SELECT=<nền tảng>:<chatId>` (chọn sẵn chat trong mục Chưa đọc/nhóm), `CHATSPACE_DEMO_FOLDER=<id>` (chọn sẵn thư mục Telegram), `CHATSPACE_DEMO_SETTINGS=storage|ignored|groups|bubbles|…` (mở Cài đặt ở trang đó), `CHATSPACE_DEMO_BUBBLE=<nền tảng>:<chatId>[:open]` (hiện bong bóng chat), `CHATSPACE_DEMO_EMPTY=1` (chưa chọn chat — màn hình chào), `CHATSPACE_DEMO_LOGIN=form|cookie|code|select|wait` (các bước đăng nhập Messenger) + `CHATSPACE_DEMO_LOGIN_ERROR=<thông báo>` (hiện lỗi đăng nhập), `CHATSPACE_DEMO_NOTIFY=<giây>` + `CHATSPACE_DEMO_NOTIFY_PLATFORM=<nền tảng>` + `CHATSPACE_DEMO_NOTIFY_COUNT=<n>` + `CHATSPACE_DEMO_NOTIFY_CHAT=<chatId>` (giả lập n tin nhắn mới vào chat đó — mặc định `c3` — cách nhau `<giây>`, để thử thông báo, bong bóng và tự cuộn xuống tin mới), `CHATSPACE_DEMO_GROUPS=<n>` (thêm n nhóm tuỳ chỉnh để thanh bên phải cuộn), `CHATSPACE_DEMO_HISTORY_PIN=1` (hiện ô khôi phục tin nhắn mã hóa của Messenger), `CHATSPACE_DEMO_FORWARD=<nền tảng>:<chatId>:<messageId>` (mở hộp chuyển tiếp cho tin đó, VD `telegram:c1:m6b` là một ảnh).

Icon app được vẽ bằng code: `swift scripts/make-icon.swift app/Resources/AppIcon.icns` (build-app.sh tự chạy nếu chưa có).

## Đăng nhập

| Nền tảng | Cách đăng nhập | Ghi chú |
|---|---|---|
| Zalo | Quét QR bằng app Zalo | Zalo chỉ cho **một** phiên web tại một thời điểm. Mở chat.zalo.me ở trình duyệt sẽ đá phiên của ChatSpace (và ngược lại); bridge tự kết nối lại sau 30 giây, tăng dần đến 10 phút. |
| WhatsApp | Quét QR: Thiết bị đã liên kết → Liên kết thiết bị | Dùng Multi-device API, điện thoại không cần online. |
| Telegram | Quét QR hoặc số điện thoại (+ mật khẩu 2 bước nếu có) | Cần **API ID/Hash** riêng tạo tại my.telegram.org — nhập ở màn hình đăng nhập hoặc Cài đặt (⌘,). |
| Messenger | Email/số điện thoại + mật khẩu ngay trong app; các bước tiếp theo (mã 2FA, chọn phương thức, captcha, "phê duyệt trên thiết bị khác") hiện lần lượt như trên messenger.com | Dùng luồng đăng nhập native của Messenger Lite (không WebView). Nếu Meta yêu cầu reCAPTCHA/Google thì phải dùng phương án dự phòng: dán cookie phiên (`c_user`, `xs`, `datr`, `fr`). |

## Thông báo & số tin chưa đọc

- Số chưa đọc lấy từ server của từng nền tảng (WhatsApp, Telegram, Messenger); Zalo không có bộ đếm phía server nên bridge tự đếm và đồng bộ khi bạn đọc trên thiết bị khác (với chat 1-1 là ước lượng).
- Huy hiệu theo cách của app gốc: Telegram đếm **số tin**, các app khác đếm **số cuộc trò chuyện**; chat tắt thông báo không được tính và có huy hiệu xám; chat "đánh dấu chưa đọc" hiện chấm. Huy hiệu Dock = tổng 4 nền tảng, được vẽ thẳng lên icon nên vẫn hiện khi quyền thông báo bị tắt (macOS ẩn huy hiệu hệ thống trong trường hợp đó).
- Chỉ báo cho tin nhắn **mới đến thời gian thực** (không báo lại tin cũ khi đồng bộ/khởi động), không báo khi bạn đang mở đúng chat đó. Khi ChatSpace đang ở trước thì chỉ phát âm báo trong app như app gốc.
- Banner nhóm theo cuộc trò chuyện, có ảnh đại diện, nút **Trả lời** và **Đánh dấu đã đọc**; tự gỡ khi chat được đọc (ở đây hoặc trên điện thoại).
- Âm báo lấy từ app gốc (WhatsApp, Telegram). Zalo PC tải âm báo từ mạng nên ChatSpace dùng âm "Pop" của macOS.
- Kiểm tra quyền thông báo ở Cài đặt (⌘,) → Thông báo.

## Tính năng của ChatSpace

- **Chưa đọc (⌘0)** — icon ChatSpace đầu thanh bên gom mọi chat chưa đọc của 4 nền tảng, chia theo nền tảng; trả lời ngay trong giao diện gốc của nền tảng đó. Chuột phải → **Bỏ qua cho lần sau** để ẩn khỏi mục này; khôi phục ở Cài đặt → **Đã bỏ qua**.
- **Nhóm tuỳ chỉnh** — nút "+" trên thanh bên: đặt tên, icon (SF Symbols hoặc ảnh tuỳ chọn), màu, từ khoá (VD `VF` gom "VF 1", "VF-2", "VF3"…), kiểu khớp (chứa / bắt đầu bằng / đầu từ — không phân biệt hoa thường, dấu) và nền tảng. Chuột phải một chat → "Thêm vào nhóm tuỳ chỉnh".
- **Thư mục Telegram** — cột thư mục bên trái giống Telegram Desktop (All Chats + thư mục của tài khoản, huy hiệu chưa đọc, xám nếu toàn chat tắt tiếng), đồng bộ khi sửa thư mục trên thiết bị khác.
- **QR tự làm mới** — Zalo, WhatsApp, Telegram tự lấy mã mới khi mã hết hạn; khi nền tảng ngừng cấp mã, app tự bắt đầu lại phiên đăng nhập (tối đa 10 lần/10 phút), sau đó hiện nút tải lại.
- **Dữ liệu và bộ nhớ** (Cài đặt) — giống Telegram: biểu đồ dung lượng theo nền tảng, xoá bộ nhớ đệm, thời gian giữ media, dung lượng tối đa, số tin lưu cục bộ, tự động tải theo loại. Media luôn lấy từ cloud khi xem; chỉ khi bắt buộc tải về (WhatsApp, Telegram) mới lưu đệm, ảnh được **siêu nén** sang HEIC 60 % (cạnh dài ≤ 1600 px). Dữ liệu tin nhắn Zalo/WhatsApp được lưu dạng gzip.
- **Trình xem ảnh & video** — bấm ảnh/video trong chat (cả trong bong bóng) để xem ngay trong ChatSpace: phóng to (⌘+ / ⌘− / ⌘0, chụm hai ngón), ← → chuyển giữa các ảnh của cuộc trò chuyện, Esc để đóng, lưu về máy, mở bằng app khác. Ảnh có **Live Text**: kéo chuột bôi đen chữ trong ảnh rồi ⌘C, hoặc bấm nút "Văn bản trong ảnh" để mở khung chữ bên cạnh (bôi đen từng đoạn, hay "Sao chép tất cả") (nhận dạng tiếng Việt bằng VisionKit, chạy trên máy). Tệp khác (PDF, tài liệu…) vẫn mở bằng app mặc định.
- **Dán ảnh / tệp** — ⌘V ảnh (ảnh chụp màn hình, ảnh sao chép từ trình duyệt/Preview) hoặc tệp đã ⌘C trong Finder vào khung soạn tin, hoặc kéo thả vào đó: ChatSpace hiện hộp xác nhận có xem trước rồi mới gửi; chữ đang soạn đi kèm làm chú thích. Chữ sao chép từ Word/Excel (có kèm ảnh của đoạn chữ) vẫn dán thành chữ như bình thường.
- **Thả cảm xúc & trả lời** — bấm nút cảm xúc khi rê chuột lên tin nhắn (Zalo: nút like ở góc bong bóng; Messenger/WhatsApp: mặt cười cạnh bong bóng; Telegram: nút trái tim cạnh bong bóng) hoặc bấm vào cảm xúc đang có để mở bảng chọn đúng bộ biểu tượng của từng app; bấm lại cảm xúc của mình để gỡ. Bấm vào khung trích dẫn trong tin trả lời để nhảy tới tin gốc. Chuột phải vào bất kỳ đâu trên tin nhắn (kể cả lên chữ) để mở menu: trả lời, thả cảm xúc, sao chép (đoạn đang bôi đen hoặc cả tin), thu hồi/xoá.
- **Sửa tin nhắn** — chuột phải tin của mình → **Chỉnh sửa**, hoặc bấm ↑ khi khung soạn tin đang trống để sửa tin gần nhất; Enter để lưu, Esc để huỷ. Giới hạn như app gốc: Telegram 48 giờ (cả chú thích ảnh/video/tệp; không giới hạn ở Saved Messages và kênh), WhatsApp và Messenger 15 phút (chỉ tin chữ; Messenger tối đa 5 lần). Zalo không hỗ trợ sửa tin.
- **Tin nhắn đã ghim** — thanh ghim dưới tiêu đề chat, vẽ theo kiểu từng app (Telegram: "Pinned Message #n" có vạch chia; Zalo: "+n ghim"; WhatsApp/Messenger: "Tin nhắn đã ghim (i/n)"). Bấm để nhảy tới tin và chuyển sang ghim kế tiếp; bấm số đếm để xem danh sách. Telegram, Zalo lấy danh sách từ server; Messenger ghi nhận ghim khi đồng bộ; WhatsApp chỉ biết các ghim xảy ra từ khi ChatSpace đăng nhập (tối đa 3, hết hạn như app gốc). Với Messenger, nhảy tới tin chỉ tải thêm tối đa 3 trang lịch sử.
- **Mở chat ở tin chưa đọc** — như Telegram: mở cuộc trò chuyện có tin chưa đọc sẽ nhảy tới vạch "Tin nhắn chưa đọc" (kiểu từng app) thay vì tin mới nhất; khi cuộn lên sẽ hiện nút mũi tên tròn ở góc dưới (kèm số tin mới đến trong lúc đang đọc) để về tin mới nhất. Tin mới chỉ tự cuộn xuống khi đang ở cuối khung chat. Chat hiện ra khi đã đứng yên đúng chỗ nên không còn nháy khi tải tin cũ hay nhảy tới tin ghim.
- **Hồ sơ thành viên** — bấm avatar hoặc tên người gửi trong chat để xem ảnh/video, tệp, link và tin nhắn người đó đã gửi trong cuộc trò chuyện; nút **Tìm kiếm** tìm cả chữ trong ảnh (OCR), nút **Nhắn tin riêng** mở (hoặc tạo, chưa gửi gì) cuộc trò chuyện riêng với người đó. Messenger chỉ mở được đoạn chat riêng đã có sẵn.
- **Album, link, cuộc gọi** — nhiều ảnh/video gửi cùng lúc (kèm chú thích) hiện thành một bong bóng dạng lưới như app gốc. Link (kể cả link ẩn sau chữ của Telegram) có màu link và con trỏ bàn tay; cuộc gọi hiện thành thẻ cuộc gọi (đỏ khi nhỡ). Nút trả lời/cảm xúc/dịch khi rê chuột nổi bên cạnh bong bóng, không làm bong bóng xê dịch.
- **Tắt thông báo cuộc trò chuyện** — chuột phải chat → **Tắt thông báo** (hoặc nút ⓘ trong chat), chọn thời lượng đúng như app gốc (Zalo: 1 giờ / 4 giờ / đến 8:00 AM / đến khi mở lại; Messenger: 15 phút / 1 / 8 / 24 giờ / đến khi bật lại; WhatsApp: 8 giờ / 1 tuần / luôn luôn; Telegram: 1 giờ / 8 giờ / 2 ngày / vĩnh viễn). Trạng thái đồng bộ lên tài khoản nên điện thoại cũng tắt theo; **Bật thông báo** để mở lại.
- **Đoạn chat Messenger mã hóa đầu cuối** — tin nhắn mới đến ngay; tin nhắn cũ (trước khi ChatSpace đăng nhập) nằm trong kho lưu trữ an toàn của Messenger và hiện **không** được tải về. Tính năng khôi phục bằng mã PIN đã bị tắt (`secureHistoryEnabled` trong `bridges/messenger/history.go`) vì Meta đã khoá tài khoản ngay sau một lần khôi phục.
- **Gắn thẻ @** — trong nhóm, gõ `@` rồi vài chữ của tên để hiện danh sách thành viên (tìm không dấu); ↑ ↓ để chọn, Enter/Tab để chèn, Esc để đóng. Người được gắn thẻ nhận thông báo như khi tag trong app gốc (Zalo, Telegram, WhatsApp, Messenger).
- **Nút của bot Telegram** — bàn phím inline hiện dưới bong bóng tin nhắn (nút callback, mở link, mở web app, trò chơi, sao chép, chuyển sang chế độ inline), bàn phím tuỳ chỉnh của bot hiện dưới khung soạn tin (bấm để gửi đúng chữ trên nút; nút bàn phím để thu gọn/mở lại; bàn phím một lần tự ẩn sau khi bấm). Câu trả lời của bot hiện thành thông báo nhỏ đầu khung chat (hoặc hộp thoại nếu bot yêu cầu). Nút thanh toán và nút chia sẻ số điện thoại/vị trí chỉ dùng được trong app Telegram nên hiện mờ.
- **Chuyển tiếp tới mọi nơi** — chuột phải tin nhắn (chữ, ảnh, video, tệp, tin thoại, sticker…) → **Chuyển tiếp…**: tìm (không dấu) và chọn nhiều cuộc trò chuyện trên cả 4 nền tảng, lọc theo nền tảng, kèm lời nhắn tuỳ chọn (gửi trước tin được chuyển tiếp); mỗi nơi nhận hiện trạng thái đã gửi / lỗi (rê chuột để xem lỗi, bấm **Gửi lại** để thử lại những nơi lỗi). Trong cùng WhatsApp hoặc Telegram dùng chuyển tiếp gốc (có nhãn "Đã chuyển tiếp", không tải lại media); sang nền tảng khác, và trong Zalo/Messenger, ChatSpace tải tệp gốc về rồi gửi lại như tin mới với tên tệp gốc — ảnh HEIC/WebP được đổi sang JPEG/PNG để nơi nhận hiện là ảnh chứ không phải tệp. Sticker động của Telegram (.tgs) đi sang nền tảng khác dưới dạng tệp.
- **Bong bóng chat** — như Telegram Android: mỗi tin nhắn mới (chat không tắt tiếng, không đang mở) làm bong bóng của chat đó bật lên ở mép màn hình trên mọi ứng dụng — kể cả khi ChatSpace đang mở — kèm thẻ xem trước nội dung trong 5 giây; tin tiếp theo làm bong bóng nảy lên và tăng số đếm. Bấm bong bóng hoặc thẻ xem trước để mở khung chat nhỏ (bố cục thu gọn riêng cho khung hẹp), bấm ra ngoài khung là tự thu lại. Mỗi bong bóng là một cửa sổ riêng: kéo một bong bóng chỉ di chuyển bong bóng đó (thả ra sẽ bám vào mép màn hình gần nhất, không đè lên bong bóng khác), kéo vào dấu × chỉ đóng bong bóng đó. Tuỳ chỉnh ở Cài đặt → Bong bóng chat (xem trước, chỉ khi ở nền, cả nhóm, số bong bóng tối đa); chuột phải chat → "Mở bong bóng chat" để mở thủ công.

## Tài nguyên gốc của các app

Để giao diện giống app gốc, ChatSpace **đọc trực tiếp** (không sao chép, không phân phối lại) tài nguyên từ các app chính thức đã cài trên máy:

| App | Lấy gì |
|---|---|
| WhatsApp.app | Icon thanh điều hướng, dấu tick, biểu tượng tắt tiếng/ghim, nút gửi/mic/emoji, nền doodle, ảnh đại diện mặc định, âm báo |
| Telegram.app | Họa tiết nền mặc định (`builtin-wallpaper-svg`), dấu tick, ghim, tắt tiếng, đính kèm, emoji, ghi âm, gửi, âm báo |
| Zalo.app | Bộ icon `zalo-font`, font Segoe UI Variable của Zalo, hình chào mừng, logo |
| Messenger | App Mac không còn được phát hành rộng rãi — logo và icon được vẽ lại bằng code |

Nếu app gốc chưa được cài, ChatSpace dùng bản vẽ lại tương đương. Icon trên thanh bên trái cũng là icon thật của app đã cài.

## Cấu trúc thư mục

```
app/                         Swift package (giao diện)
  Sources/ChatSpace/Core/    BridgeProcess, PlatformSession, Models, Notifications, NativeAssets, DemoData
  Sources/ChatSpace/Core/Workspace.swift  nhóm tuỳ chỉnh, chat bỏ qua, cài đặt bộ nhớ & bong bóng
  Sources/ChatSpace/UI/      Zalo/ Messenger/ WhatsApp/ Telegram/ Shared/ ChatSpace/ (Chưa đọc, nhóm, bong bóng)
bridges/node/src/            main.ts (vòng RPC), adapter.ts, media-cache.ts, zalo*.ts, whatsapp*.ts, telegram*.ts
bridges/messenger/           Go: main.go, bridge.go, tables.go, …
bridges/messenger/third_party/mautrix-meta/  bản mautrix-meta v0.2610.0 có vá (xem bên dưới)
docs/PROTOCOL.md             đặc tả giao thức app ↔ bridge
scripts/build-app.sh         đóng gói .app
scripts/make-icon.swift      vẽ icon ChatSpace
```

## Lưu ý

- **mautrix-meta được vá tại chỗ** (`bridges/messenger/third_party/mautrix-meta`, `replace` trong `go.mod`): trình thông dịch Bloks của upstream báo lỗi `expected int64 or float64 in jmu lhs, got <nil>` khi bấm nút đăng nhập vì script của Meta cộng vào biến chưa gán; bản vá coi `null` là 0 như runtime của Meta (`pkg/messagix/bloks/interp.go`, tìm "ChatSpace patch"). Khi nâng cấp mautrix-meta phải chép bản mới vào đó và vá lại nếu upstream chưa sửa.
- **Lịch sử Zalo nằm trên điện thoại**: chat 1-1 chỉ nằm trên điện thoại, còn API lịch sử nhóm (`/api/group/history`) hiện trả 404, nên sau khi đăng nhập mới ChatSpace chỉ có tin nhắn đến từ lúc đó. Để lấy tin cũ, bấm **⋯ → Đồng bộ tin nhắn từ điện thoại** (hoặc nút trên khung "Đồng bộ tin nhắn" đầu danh sách chat) rồi bấm **Đồng ý** trong Zalo trên điện thoại: ChatSpace làm như Zalo PC — nhờ điện thoại gửi toàn bộ lịch sử qua máy chủ Zalo, tải về, giải mã và lưu vào máy, kể cả chat với người chưa kết bạn. Việc giải mã dùng thư viện của Zalo PC, nên cần cài Zalo PC (`/Applications/Zalo.app`; chỉ đọc, không cần mở hay đăng nhập). Không đọc trực tiếp được dữ liệu của Zalo PC: Zalo PC mã hoá cơ sở dữ liệu (SQLCipher) bằng khoá `dkey` cấp riêng cho phiên đăng nhập của chính Zalo PC, chỉ nằm trong bộ nhớ khi app chạy; khoá của phiên web mà ChatSpace dùng không mở được các DB này (đã thử).
- Bridge Node chạy với `--smol` (Bun dọn bộ nhớ thường xuyên hơn); ảnh trong app được giải mã đúng kích thước hiển thị và bộ đệm ảnh giới hạn 96 MB.
- **Không để việc ghi đĩa chặn tin nhắn đến**: các bridge lưu lịch sử dưới dạng JSON nén; trước đây mỗi lần ghi (1,5–2 giây sau tin mới) toàn bộ kho được `JSON.stringify` một lượt trên luồng chính — với kho Zalo sau khi đồng bộ từ điện thoại (hàng trăm nghìn tin, đo được 0,4–3,5 giây cho 150k–400k tin) hoặc nhóm WhatsApp lớn, mọi tin đến trong lúc đó phải chờ. Nay Zalo ghi theo từng chat và nhả vòng lặp sự kiện sau mỗi 8 ms, WhatsApp chuỗi hoá từng chat ngay trước khi nén; Messenger chỉ giữ khoá kho trong lúc sao chép chứ không trong lúc mã hoá JSON. Sticker Zalo chưa có trong bộ đệm được tải ngoài hàng đợi nhận tin (trước đây một sticker lạ có thể giữ các tin sau nó tới 5 giây). Thông báo hiện ngay, không chờ tải ảnh đại diện (trước đây chờ tới 1,5 giây với avatar Zalo/Messenger). Mỗi tin đến thời gian thực được ghi vào `bridge.log` kèm độ trễ so với lúc gửi (`live message … delivered N ms after its send time`) để đo khi cần.
- **Bắt kịp tin nhắn sau khi mất kết nối** (máy ngủ, đổi mạng): app gọi `connection.resume` cho mọi bridge ngay khi máy thức dậy hoặc mạng thay đổi; bridge kiểm tra kết nối (ping có thời hạn) và kết nối lại tức thì thay vì chờ keep-alive của thư viện (GramJS 9 giây, Baileys 35 giây, cộng thời gian lùi của bridge). Riêng Telegram còn lưu vị trí trong luồng cập nhật (pts, kể cả pts từng kênh, tại `telegram/updates-state.json`) và gọi `updates.getDifference` / `getChannelDifference` khi kết nối (lại) hoặc phát hiện thiếu cập nhật, đúng như app chính thức — tin đến trong lúc mất kết nối được giao ngay (có thông báo nếu còn mới) kèm số chưa đọc chính xác, thay vì chỉ xuất hiện khi mở lại chat. Xem [docs/PROTOCOL.md](docs/PROTOCOL.md#keeping-up-after-a-disconnect).


Zalo, WhatsApp và Messenger không có API chính thức cho client bên thứ ba; các thư viện này mô phỏng client web/desktop. Dùng cho tài khoản cá nhân, tránh gửi hàng loạt — các nền tảng có thể khoá tài khoản có hành vi bất thường. Telegram dùng MTProto chính thức nên không có rủi ro này.
