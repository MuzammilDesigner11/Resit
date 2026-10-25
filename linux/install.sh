#!/usr/bin/env bash
# Installs Resit for the current user (no root needed): the app goes to ~/.local/share/resit and
# a `resit` command to ~/.local/bin. It asks whether to add Resit to the app menu and to put an
# icon on the desktop.
#
# Usage: ./install.sh                     install or update, asking about shortcuts
#        ./install.sh --menu --desktop    choose without asking (--no-menu / --no-desktop to skip)
#        ./install.sh --remove            uninstall (instances and the Android SDK are kept)
set -euo pipefail

HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
DATA=${XDG_DATA_HOME:-$HOME/.local/share}
APP_DIR=$DATA/resit
BIN_DIR=$HOME/.local/bin
MENU_ENTRY=$DATA/applications/resit.desktop
ICON=$DATA/icons/hicolor/256x256/apps/resit.png
DESKTOP_DIR=$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")
DESKTOP_ICON=$DESKTOP_DIR/resit.desktop

MENU=""
DESKTOP=""
for arg in "$@"; do
    case "$arg" in
        --menu) MENU=1 ;;
        --no-menu) MENU=0 ;;
        --desktop) DESKTOP=1 ;;
        --no-desktop) DESKTOP=0 ;;
        --remove)
            rm -rf "$APP_DIR" "$MENU_ENTRY" "$ICON" "$BIN_DIR/resit" "$DESKTOP_ICON"
            command -v update-desktop-database >/dev/null && update-desktop-database -q "$DATA/applications" || true
            echo "Resit removed. Your instances (~/.android/avd), the Android SDK and ~/.config/Resit were left alone."
            exit 0 ;;
        -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $arg (see --help)" >&2; exit 1 ;;
    esac
done

[[ -f $HERE/Resit ]] || { echo "Run this from the extracted Resit folder (Resit binary not found)." >&2; exit 1; }

# Asks a yes/no question (default yes); without a terminal to ask in, takes the default.
ask() {
    local answer
    if [[ ! -t 0 ]]; then echo 1; return; fi
    read -rp "$1 [Y/n] " answer </dev/tty
    [[ $answer =~ ^[Nn] ]] && echo 0 || echo 1
}
[[ -n $MENU ]] || MENU=$(ask "Add Resit to the app menu?")
[[ -n $DESKTOP ]] || DESKTOP=$(ask "Put a Resit icon on the desktop?")

mkdir -p "$APP_DIR" "$BIN_DIR" "$(dirname "$ICON")"
install -m 755 "$HERE/Resit" "$APP_DIR/Resit"
install -m 755 "$HERE/setup.sh" "$APP_DIR/setup.sh"
install -m 644 "$HERE/resit.png" "$ICON"
ln -sf "$APP_DIR/Resit" "$BIN_DIR/resit"
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$DATA/icons/hicolor" 2>/dev/null || true

ENTRY=$(sed "s|@EXEC@|$APP_DIR/Resit|" "$HERE/resit.desktop")
if [[ $MENU == 1 ]]; then
    mkdir -p "$(dirname "$MENU_ENTRY")"
    echo "$ENTRY" > "$MENU_ENTRY"
    echo "Added Resit to the app menu."
else
    rm -f "$MENU_ENTRY"
fi
command -v update-desktop-database >/dev/null && update-desktop-database -q "$DATA/applications" || true

if [[ $DESKTOP == 1 ]]; then
    mkdir -p "$DESKTOP_DIR"
    echo "$ENTRY" > "$DESKTOP_ICON"
    chmod 755 "$DESKTOP_ICON"
    # GNOME only launches desktop icons it has been told to trust.
    command -v gio >/dev/null && gio set "$DESKTOP_ICON" metadata::trusted true 2>/dev/null || true
    echo "Put a Resit icon on the desktop."
else
    rm -f "$DESKTOP_ICON"
fi

echo "Installed Resit to $APP_DIR."
if [[ ! -x ${ANDROID_HOME:-$HOME/Android/Sdk}/emulator/emulator ]]; then
    echo "Next: download the Android Emulator with  $APP_DIR/setup.sh  (or the Run setup button in Resit)."
fi
echo "Start it from the app menu or desktop, or run: resit"
