#!/bin/sh
# Builds dist/ChatSpace-win-x64.exe: one self-contained Windows 10/11 x64 executable with the
# Zalo/WhatsApp/Telegram (Bun) and Messenger (Go) bridges embedded. Runs on macOS or Linux.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
root=$(dirname "$here")
out="$here/build"
mkdir -p "$out/bridges"

(cd "$root/bridges/node" && bun build src/main.ts --compile --compile-exec-argv=--smol \
  --target=bun-windows-x64 --outfile "$out/bridges/chatspace-bridge.exe")
(cd "$root/bridges/messenger" && GOOS=windows GOARCH=amd64 CGO_ENABLED=0 \
  go build -trimpath -ldflags "-s -w" -o "$out/bridges/chatspace-messenger.exe" .)

(cd "$here/ChatSpace" && dotnet publish -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o "$out/publish")

mkdir -p "$root/dist"
cp "$out/publish/ChatSpace.exe" "$root/dist/ChatSpace-win-x64.exe"
echo "$root/dist/ChatSpace-win-x64.exe"
