#!/usr/bin/env bash
# Builds dist/ChatSpace.app: Swift UI + compiled bridges (no runtime dependencies).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${CONFIG:-release}"
APP="$ROOT/dist/ChatSpace.app"
BUN="${BUN:-$(command -v bun || echo "$HOME/.bun/bin/bun")}"

echo "==> Swift app ($CONFIG)"
(cd "$ROOT/app" && swift build -c "$CONFIG")
BIN="$(cd "$ROOT/app" && swift build -c "$CONFIG" --show-bin-path)/ChatSpace"

echo "==> Node bridge (Zalo, WhatsApp, Telegram)"
(cd "$ROOT/bridges/node" && "$BUN" install --frozen-lockfile >/dev/null && \
  "$BUN" build src/main.ts --compile --minify --compile-exec-argv="--smol" --outfile dist/chatspace-bridge)

echo "==> Messenger bridge (Go)"
(cd "$ROOT/bridges/messenger" && go build -trimpath -ldflags "-s -w" -o chatspace-messenger .)

echo "==> Bundle"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/bridges"
cp "$BIN" "$APP/Contents/MacOS/ChatSpace"
cp "$ROOT/bridges/node/dist/chatspace-bridge" "$APP/Contents/Resources/bridges/"
cp "$ROOT/bridges/messenger/chatspace-messenger" "$APP/Contents/Resources/bridges/"
[ -f "$ROOT/app/Resources/AppIcon.icns" ] || swift "$ROOT/scripts/make-icon.swift" "$ROOT/app/Resources/AppIcon.icns"
cp "$ROOT/app/Resources/AppIcon.icns" "$APP/Contents/Resources/"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>ChatSpace</string>
  <key>CFBundleDisplayName</key><string>ChatSpace</string>
  <key>CFBundleIdentifier</key><string>vn.chatspace.app</string>
  <key>CFBundleExecutable</key><string>ChatSpace</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>ChatSpace ghi âm tin nhắn thoại khi bạn bấm nút micro.</string>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.social-networking</string>
</dict>
</plist>
PLIST

# Ad-hoc signature so macOS runs it locally and notifications work.
codesign --force --deep --sign - "$APP" >/dev/null
echo "==> $APP"
