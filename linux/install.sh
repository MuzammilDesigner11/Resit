#!/usr/bin/env bash
# Installs Resit for the current user (no root needed): the app goes to ~/.local/share/resit,
# a `resit` command to ~/.local/bin, and Resit appears in the desktop's app menu.
#
# Usage: ./install.sh            install or update
#        ./install.sh --remove   uninstall (instances and the Android SDK are kept)
set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}
APP_DIR=$DATA/resit
BIN_DIR=$HOME/.local/bin
DESKTOP=$DATA/applications/resit.desktop
ICON=$DATA/icons/hicolor/256x256/apps/resit.png

if [[ ${1:-} == --remove ]]; then
    rm -rf "$APP_DIR" "$DESKTOP" "$ICON" "$BIN_DIR/resit"
    command -v update-desktop-database >/dev/null && update-desktop-database -q "$DATA/applications" || true
    echo "Resit removed. Your instances (~/.android/avd), the Android SDK and ~/.config/Resit were left alone."
    exit 0
fi

[[ -f $HERE/Resit ]] || { echo "Run this from the extracted Resit folder (Resit binary not found)." >&2; exit 1; }

mkdir -p "$APP_DIR" "$BIN_DIR" "$(dirname "$DESKTOP")" "$(dirname "$ICON")"
install -m 755 "$HERE/Resit" "$APP_DIR/Resit"
install -m 755 "$HERE/setup.sh" "$APP_DIR/setup.sh"
install -m 644 "$HERE/resit.png" "$ICON"
ln -sf "$APP_DIR/Resit" "$BIN_DIR/resit"
sed "s|@EXEC@|$APP_DIR/Resit|" "$HERE/resit.desktop" > "$DESKTOP"
command -v update-desktop-database >/dev/null && update-desktop-database -q "$DATA/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$DATA/icons/hicolor" 2>/dev/null || true

echo "Installed Resit to $APP_DIR."
if [[ ! -x ${ANDROID_HOME:-$HOME/Android/Sdk}/emulator/emulator ]]; then
    echo "Next: download the Android Emulator with  $APP_DIR/setup.sh  (or the Run setup button in Resit)."
fi
echo "Start it from your app menu, or run: resit"
