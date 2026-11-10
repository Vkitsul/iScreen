#!/bin/bash
# Builds iScreen.app (needs the Xcode Command Line Tools: xcode-select --install).
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release

APP=build/iScreen.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/iScreen "$APP/Contents/MacOS/iScreen"
cp Info.plist "$APP/Contents/Info.plist"
# Ad-hoc signature: enough to run locally and to get the Local Network permission prompt.
codesign --force --sign - "$APP"

echo "OK: $(pwd)/$APP"
echo "Avvia con: open \"$(pwd)/$APP\""
