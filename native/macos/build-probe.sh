#!/bin/sh
set -eu

project_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
build_dir="$project_root/.build/macos"
output_dir="$project_root/dist/macos"
output="$output_dir/switchdesk-probe"

mkdir -p "$build_dir" "$output_dir"
sdk_path=$(xcrun --sdk macosx --show-sdk-path)
xcrun --sdk macosx swiftc \
  -target arm64-apple-macos13.0 \
  -sdk "$sdk_path" \
  -module-cache-path "$build_dir/module-cache" \
  -o "$output" \
  "$project_root/native/macos/Probe.swift"
printf 'Built %s\n' "$output"
