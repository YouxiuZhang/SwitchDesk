#!/bin/sh
set -eu
project_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
build_dir="$project_root/.build/macos-app"
app="${SWITCHDESK_APP_OUTPUT:-$project_root/dist/product/mac/双栖.app}"
mkdir -p "$build_dir/module-cache" "$app/Contents/MacOS" "$app/Contents/Resources"
sdk_path=$(xcrun --sdk macosx --show-sdk-path)
xcrun --sdk macosx swiftc -swift-version 5 -O -target arm64-apple-macos13.0 \
  -sdk "$sdk_path" -module-cache-path "$build_dir/module-cache" \
  -framework AppKit -framework Network -framework Security -framework ScreenCaptureKit \
  -framework CoreMedia -framework CoreVideo -framework CoreImage \
  -o "$build_dir/SwitchDesk" "$project_root"/native/macos/app/*.swift
cp "$build_dir/SwitchDesk" "$app/Contents/MacOS/SwitchDesk"
cp "$project_root/native/macos/app/Info.plist" "$app/Contents/Info.plist"
if [ -f "$project_root/native/macos/SwitchDesk.icns" ]; then
  cp "$project_root/native/macos/SwitchDesk.icns" "$app/Contents/Resources/SwitchDesk.icns"
fi
cp "$project_root/native/macos/SwitchDesk.png" "$app/Contents/Resources/SwitchDesk.png"
if [ -f "$project_root/native/macos/README.zh-CN.md" ]; then
  cp "$project_root/native/macos/README.zh-CN.md" "$app/Contents/Resources/使用说明.md"
fi
codesign --force --sign - --identifier com.switchdesk.desktop "$app"
codesign --verify --strict --verbose=2 "$app"
printf 'Built %s\n' "$app"
