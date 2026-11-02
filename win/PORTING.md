# Porting SwiftUI views to the Windows build

The Windows app (`win/ChatSpace`, Avalonia 11.3 on .NET 10) must look identical to the macOS SwiftUI
app in `app/Sources/ChatSpace`. Each Swift view is ported line by line to C# using the Kit in
`win/ChatSpace/Kit`, a SwiftUI emulation. The model is the finished Zalo port:
`UI/Zalo/ZaloViews.cs` ⇄ `app/Sources/ChatSpace/UI/Zalo/ZaloViews.swift`. Read both before starting.

## Build and render

```sh
cd <your project copy>/ChatSpace
dotnet build -c Debug -f net10.0 2>&1 | grep -E " error |Error\(s\)"
CHATSPACE_PLATFORM=telegram dotnet bin/Debug/net10.0/ChatSpace.dll --screenshot /tmp/x.png   # demo data, 1280x820 @2x
```

- `--window <name>` captures another window registered in `App.ScreenshotTargets`.
- `--wait <ms>` sets the settle time (default 1500).
- `CHATSPACE_PLATFORM` takes `zalo`, `messenger`, `whatsapp`, `telegram` or `inbox`.
- Other demo switches: `CHATSPACE_DEMO_EMPTY=1` (no chat open) and `CHATSPACE_DEMO_LOGIN=form|cookie|code|select|wait`
  (Messenger onboarding), plus `CHATSPACE_DEMO_LOGIN_ERROR=…`. More are in `Core/DemoData.cs` and `App/AppStartup.cs`:
  `CHATSPACE_DEMO_SETTINGS=<section>`, `CHATSPACE_DEMO_SELECT=<platform>:<chatId>`, `CHATSPACE_DEMO_FORWARD=…`,
  `CHATSPACE_DEMO_COST=…` and `CHATSPACE_DEMO_BUBBLE=…`.
- The macOS reference captures (2560×1640, @2x) are in `/tmp/cs-ref/<name>/main-ChatSpace.png`. Extra
  windows are `w1-*.png`.
- Compare crops side by side with PIL. The Read tool sometimes reports a freshly written PNG as
  missing for a few seconds; wait and retry, or write to a new file name.
- Never touch or launch `dist/ChatSpace.app` or anything with bundle id `vn.chatspace.app`. Demo mode never contacts
  real services.

## Kit cheat sheet (`using static ChatSpace.Kit.UI;`)

| SwiftUI | C# |
|---|---|
| `VStack(alignment: .leading, spacing: 4) {…}` | `VStack(HAlign.Leading, 4, a, b, c)`. Null children are skipped, so use `cond ? x : null` |
| `HStack(alignment: .top, spacing: 8)` | `HStack(VAlign.Top, 8, …)` |
| `ZStack(alignment: .topLeading)` | `ZStack(Alignment.TopLeading, …)` |
| `Spacer()` / `Spacer(minLength: 0)` | `Spacer()` / `Spacer(0)` |
| `Text("x").font(.system(size: 13, weight: .semibold))` | `Text("x").Font(13, Weight.Semibold)` or `.Font(SFont)` |
| `.foregroundStyle(.secondary)` | `.Foreground(SColor.Secondary)` |
| `Color(hex: 0x123456)` | `SColor.Hex(0x123456)` (alpha arg), `.Opacity(x)` |
| `Image(systemName:)` | `Image("name")` (SF Symbol masks are bundled; check `Symbol.Exists`) |
| `.frame(width:height:alignment:)`, `.frame(maxWidth: .infinity, alignment: .leading)` | `.Frame(width:, height:, alignment:)`, `.FillWidth(Alignment.Leading)` |
| `.padding(.horizontal, 12)` | `.PaddingH(12)`; also `PaddingV/Top/Bottom/Leading/Trailing`, `Padding(top, leading, bottom, trailing)` |
| `.background(Color)` / `.background(RoundedRectangle(cornerRadius: 8).fill(c))` | `.Background(c)` / `.Background(RoundedRectangle(8).Fill(c))` |
| `.overlay(alignment: .bottom) { … }` | `.Overlay(Alignment.Bottom, ctrl)` |
| `.clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))` | `.ClipRounded(8, true)`; also `ClipCircle()` |
| `@State var x = false` | `var x = Remember(false);` inside a `View(() => …)` body, read and write through `x.Value` |
| a computed `body` that observes session state | `View(() => …)`, which rebuilds when any observed property it read changes |
| `ForEach(list, id:)` | `ForEach(() => list, x => x.Id, x => row)` |
| `ScrollView` | `ScrollView(content)` |
| `Button(action:) { label }.buttonStyle(.plain)` | `Button(action, label)` |
| `.onTapGesture`, `.onHover`, `.onAppear`, `.onDisappear`, `.help` | `.OnTap`, `.OnHover`, `.OnAppear`, `.OnDisappear`, `.Help` |
| `.contextMenu` | `.ContextMenu(() => entries)` with `MenuEntry.Item/Separator/Sub` |
| `.popover(isPresented:)` | `.Popover(state, done => content)`; a tool icon with a popover is `.ToolPopover(help, state, done => …)` |
| `.tool(help) { }` (icon button with hover) | `.Tool(help, action, size)` |
| hover-only visibility | `.VisibleWhen(() => hovered.Value)` + `.TrackHover(hovered)` on the row |
| hover background | `.HoverBackground(normal, hover, radius)` |
| `.lineLimit(1)`, `.multilineTextAlignment(.center)` | `.LineLimit(1)`, `.MultilineAlignment(TextAlignment.Center)` |
| `.environment(\.colorScheme, .light)` | `.Scheme(Scheme.Light)` |
| `.shadow(color:radius:x:y:)` | `.Shadow(color, radius, x, y)` |
| `LinearGradient` | `new LinearGradient([c1, c2], LinearGradient.Top, LinearGradient.Bottom)` |

- Alignment names are `Center, Leading, Trailing, Top, Bottom, TopLeading, TopTrailing, BottomLeading,
  BottomTrailing`. There is no Left, Right or TopLeft.
- Controls that must keep focus or state across rebuilds (text fields, the composer, search boxes) go in
  `Keep(key, () => control)` or come from `Composer.Editor(…)`.
- A child control instance can only have one parent. Never put the same control object in two places, and never
  reuse one across a rebuild except through `Keep`.

## Shared pieces already ported (in `UI/Shared`). Use them; don't re-port.

- **ConversationKit.cs:**
  - `ChatScroll` (message list, paging, scroll-to-bottom), `MessageRow` and `MessageGrouping`.
  - `BubbleColumn`, `CapWidth`, `ChatMetrics`, `CompactAware`.
  - `MediaContent`, `FileContent`, `AvatarView`, `BridgeStatusView`, `QRExpiryOverlay`.
  - `ReactionSets`, `ReactionPicker`, `.WithReactionPicker`, `.MessageContextMenu(session, msg, MenuLabels.X, reactions)`.
  - `DoodleWallpaper`, `Shapes.Bubble/Tail/RoundedRect`, `SeededRandom`, `Clip`.
- **Composer.cs:** `Composer.Editor(...)`, `Composer.Draft`, `Composer.Emoji`, `.ComposerTapArea()`.
- **ChatTools.cs:**
  - `ChatTools.NativeOnly/Call/Screenshot/Attach/SendPasted/PickFile`.
  - `ComposerAttachments`, `VoiceRecorder`, `VoiceTools.VoiceTool`, `InChatSearch`, `ChatInfoPanel`.
- **SharedPieces.cs:**
  - Translation: `TranslateToggle`, `TranslationQuote`, `OutgoingLanguageButton`, `TranslationNotice`.
  - `MathSendPreview`, `MentionSuggestions` / `.WithMentionSuggestions`.
  - `ChatMenuItems` / `.ChatContextMenu(platform, chat, inInbox)`.
- **Other:**
  - Platform assets: `Core/NativeAssets.cs`, covering `NativeAssets.Shared`, the WhatsApp and Telegram catalogs, and app icons. `NativeImage(catalog, name)` is in `Kit/Views.cs`.
  - Fonts: `SFont`, plus `ZaloFont` for Zalo.
  - Models: `Core/Models.cs`. Session API: `Core/PlatformSession.cs`. App state: `Core/AppModel.cs`, `Core/Workspace.cs`.

## Rules

- Port everything: every view, state, string (Vietnamese, exact), colour, size and padding. Do not simplify.
- Keep platform-specific helpers inside your own file(s) with platform-prefixed names (e.g. `TG`, `TelegramBubble`).
  Do not edit shared files (`Kit/*`, `UI/Shared/*`, `Core/*`) in your copy. If you need a Kit or Core change,
  implement it locally in your file if possible; otherwise describe the exact change in your final report.
- macOS-only actions such as `NSWorkspace`, `NSSound` or `screencapture` map to the existing helpers (`KitShell.Open`,
  `ChatTools.*`). If no equivalent exists, keep the UI and call the closest helper.
- Remove your view's stub from `UI/Pending.cs` in your copy, and say so in your report.
