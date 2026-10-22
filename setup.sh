#!/usr/bin/env bash
# Installs the Android SDK pieces needed to run an emulator on Linux and creates the "Resit"
# instance, tuned to use as much of the host machine as is sensible.
#
#   - Downloads the Android command-line tools.
#   - Installs platform-tools, the emulator and the newest stable x86_64 system image.
#   - Checks KVM (hardware acceleration) and the libraries the emulator needs.
#   - Writes the instance's config.ini sized from the host's CPU, RAM and disk.
#   - Saves the chosen paths to resit.local.json so Resit finds everything.
#
# Usage: ./setup.sh [--sdk-root DIR] [--avd-home DIR] [--api-level 36] [--image-type google_apis]
#                   [--cores N] [--ram-mb N] [--data-gb N] [--name Resit] [--force]
set -euo pipefail

AVD_NAME=Resit
SDK_ROOT=${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}
AVD_HOME=${ANDROID_AVD_HOME:-$HOME/.android/avd}
API_LEVEL=""
IMAGE_TYPE=google_apis_playstore
LCD_WIDTH=1920
LCD_HEIGHT=1080
LCD_DENSITY=280
CORES=0
RAM_MB=0
DATA_GB=0
FORCE=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --sdk-root) SDK_ROOT=$2; shift 2 ;;
        --avd-home) AVD_HOME=$2; shift 2 ;;
        --api-level) API_LEVEL=$2; shift 2 ;;
        --image-type) IMAGE_TYPE=$2; shift 2 ;;
        --cores) CORES=$2; shift 2 ;;
        --ram-mb) RAM_MB=$2; shift 2 ;;
        --data-gb) DATA_GB=$2; shift 2 ;;
        --name) AVD_NAME=$2; shift 2 ;;
        --force) FORCE=1; shift ;;
        -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1 (see --help)" >&2; exit 1 ;;
    esac
done

case "$IMAGE_TYPE" in
    google_apis_playstore|google_apis|default) ;;
    *) echo "--image-type must be google_apis_playstore, google_apis or default" >&2; exit 1 ;;
esac

REPO_URL=https://dl.google.com/android/repository
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
CONFIG_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/Resit

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
warn() { printf '\033[33mWarning: %s\033[0m\n' "$1"; }
die() { printf '\033[31m%s\033[0m\n' "$1" >&2; exit 1; }

for tool in curl unzip; do
    command -v "$tool" >/dev/null || die "Please install $tool first (e.g. sudo apt install $tool, sudo dnf install $tool)."
done

# ---- Host profile -------------------------------------------------------------------------

LOGICAL=$(nproc)
# Distinct (socket, core) pairs = physical cores.
PHYSICAL=$(awk -F: '/^physical id/{p=$2} /^core id/{print p "-" $2}' /proc/cpuinfo | sort -u | wc -l)
[[ $PHYSICAL -gt 0 ]] || PHYSICAL=$(( LOGICAL > 1 ? LOGICAL / 2 : 1 ))
HOST_RAM_MB=$(( $(awk '/^MemTotal/{print $2}' /proc/meminfo) / 1024 ))
CPU_NAME=$(awk -F': ' '/^model name/{print $2; exit}' /proc/cpuinfo)

# One vCPU per physical core: past that, SMT siblings add contention rather than speed.
if [[ $CORES -le 0 ]]; then CORES=$(( PHYSICAL < 2 ? 2 : PHYSICAL > 16 ? 16 : PHYSICAL )); fi
# 4 GB suits most games; Quick Boot snapshots hold all guest RAM, so more slows every start.
if [[ $RAM_MB -le 0 ]]; then
    RAM_MB=$(( HOST_RAM_MB / 2 / 1024 * 1024 ))
    (( RAM_MB > 4096 )) && RAM_MB=4096
    (( RAM_MB < 2048 )) && RAM_MB=2048
fi
mkdir -p "$SDK_ROOT" "$AVD_HOME"
if [[ $DATA_GB -le 0 ]]; then
    # 32 GB of app storage, unless the drive is short on space.
    FREE_GB=$(( $(df -Pk "$AVD_HOME" | awk 'NR==2{print $4}') / 1024 / 1024 ))
    DATA_GB=$(( FREE_GB < 60 ? 8 : 32 ))
fi

echo
echo "Host   : $CPU_NAME (${PHYSICAL}C/${LOGICAL}T, $(( HOST_RAM_MB / 1024 )) GB RAM)"
echo "Guest  : $CORES vCPU, $RAM_MB MB RAM, $DATA_GB GB data, ${LCD_WIDTH}x${LCD_HEIGHT} @ $LCD_DENSITY dpi, host GPU rendering"
echo "SDK    : $SDK_ROOT"
echo "AVDs   : $AVD_HOME"
echo

export ANDROID_HOME=$SDK_ROOT ANDROID_SDK_ROOT=$SDK_ROOT ANDROID_AVD_HOME=$AVD_HOME

# ---- Command-line tools -------------------------------------------------------------------

TOOLS_BIN=$SDK_ROOT/cmdline-tools/latest/bin
if [[ ! -x $TOOLS_BIN/android && ! -x $TOOLS_BIN/sdkmanager ]]; then
    step "Downloading Android command-line tools"
    TMP=$(mktemp -d)
    trap 'rm -rf "$TMP"' EXIT
    curl -fsSL "$REPO_URL/repository2-3.xml" -o "$TMP/repo.xml"
    FILE=$(awk '/<remotePackage path="cmdline-tools;latest">/{f=1} f&&/<\/remotePackage>/{exit} f' "$TMP/repo.xml" \
        | grep -o 'commandlinetools-linux-[0-9]*_latest\.zip' | head -1)
    [[ -n $FILE ]] || die "Could not find the Linux command-line tools in Google's repository."
    curl -fL --retry 5 --progress-bar "$REPO_URL/$FILE" -o "$TMP/$FILE"
    unzip -q "$TMP/$FILE" -d "$TMP"
    rm -rf "$SDK_ROOT/cmdline-tools/latest"
    mkdir -p "$SDK_ROOT/cmdline-tools"
    mv "$TMP/cmdline-tools" "$SDK_ROOT/cmdline-tools/latest"
fi

# The native `android` CLI needs no Java; older tool versions only have sdkmanager (Java 17+).
if [[ -x $TOOLS_BIN/android ]]; then
    sdk_install() { "$TOOLS_BIN/android" --no-metrics "--sdk=$SDK_ROOT" sdk install "$@"; }
    sdk_list() { "$TOOLS_BIN/android" --no-metrics "--sdk=$SDK_ROOT" sdk list --all 2>&1; }
else
    command -v java >/dev/null || die "These command-line tools need Java 17 or newer (e.g. sudo apt install openjdk-17-jre-headless)."
    # `yes` dies of SIGPIPE when sdkmanager is done, which pipefail would count as a failure.
    sdk_install() {
        set +o pipefail
        yes | "$TOOLS_BIN/sdkmanager" "--sdk_root=$SDK_ROOT" "$@"
        local rc=$?
        set -o pipefail
        return $rc
    }
    sdk_list() { "$TOOLS_BIN/sdkmanager" "--sdk_root=$SDK_ROOT" --list 2>&1; }
fi

# ---- System image -------------------------------------------------------------------------

step "Finding the newest stable system image"
if [[ -n $API_LEVEL ]]; then
    API_ID=android-$API_LEVEL
else
    # android-35 or android-37.0, but not -extNN, -betaN or the _ps16k / _tablet variants.
    API_ID=$(sdk_list | grep -oE "system-images[/;]android-[0-9]+(\.[0-9]+)?[/;]$IMAGE_TYPE[/;]x86_64( |\||$)" \
        | grep -oE 'android-[0-9]+(\.[0-9]+)?' | sort -t- -k2 -V -u | tail -1 || true)
    [[ -n $API_ID ]] || die "No stable x86_64 $IMAGE_TYPE system image found."
fi
echo "    $API_ID / $IMAGE_TYPE"

echo
echo "Installing Android SDK packages means accepting the Android SDK License Agreement:"
echo "    https://developer.android.com/studio/terms"
echo
step "Installing platform-tools, emulator and the system image (several GB on first run)"
if [[ -x $TOOLS_BIN/android ]]; then
    sdk_install platform-tools emulator "system-images/$API_ID/$IMAGE_TYPE/x86_64"
else
    sdk_install platform-tools emulator "system-images;$API_ID;$IMAGE_TYPE;x86_64"
fi
IMAGE_DIR=$SDK_ROOT/system-images/$API_ID/$IMAGE_TYPE/x86_64
[[ -f $IMAGE_DIR/system.img ]] || die "System image did not install to $IMAGE_DIR"
EMULATOR=$SDK_ROOT/emulator/emulator

# ---- Acceleration and libraries -----------------------------------------------------------

step "Checking hardware acceleration (KVM)"
ACCEL=$("$EMULATOR" -accel-check 2>&1 || true)
if grep -q "is installed and usable" <<<"$ACCEL"; then
    printf '    \033[32m%s\033[0m\n' "$(grep -m1 usable <<<"$ACCEL")"
    ACCEL_OK=1
else
    ACCEL_OK=0
    warn "KVM is not usable, so the emulator will be extremely slow."
    echo "$ACCEL"
    if [[ ! -e /dev/kvm ]]; then
        echo "  /dev/kvm is missing: turn on virtualization (Intel VT-x / AMD SVM) in the BIOS, and"
        echo "  check the kvm_intel or kvm_amd module is loaded (lsmod | grep kvm)."
    elif [[ ! -r /dev/kvm || ! -w /dev/kvm ]]; then
        echo "  Your user cannot open /dev/kvm. Run:  sudo usermod -aG kvm \$USER   then log out and back in."
    fi
fi

QEMU=$SDK_ROOT/emulator/qemu/linux-x86_64/qemu-system-x86_64
if [[ -x $QEMU ]]; then
    MISSING=$(LD_LIBRARY_PATH="$SDK_ROOT/emulator/lib64:$SDK_ROOT/emulator/lib64/qt/lib" ldd "$QEMU" 2>/dev/null | awk '/not found/{print $1}' | sort -u || true)
    if [[ -n $MISSING ]]; then
        warn "The emulator needs these libraries:"
        echo "$MISSING" | sed 's/^/    /'
        echo "  On Debian/Ubuntu: sudo apt install libpulse0 libnss3 libxkbfile1 libxcb-cursor0 libgl1"
        echo "  On Fedora:        sudo dnf install pulseaudio-libs nss libxkbfile xcb-util-cursor mesa-libGL"
    fi
fi
command -v pactl >/dev/null || echo "    Tip: install pactl (pulseaudio-utils) to choose sound devices from Resit."

# ---- Instance -----------------------------------------------------------------------------

AVD_DIR=$AVD_HOME/$AVD_NAME.avd
AVD_INI=$AVD_HOME/$AVD_NAME.ini
if [[ -d $AVD_DIR && $FORCE -eq 1 ]]; then
    step "Deleting existing instance '$AVD_NAME'"
    rm -rf "$AVD_DIR"
fi
step "Writing instance '$AVD_NAME'"
mkdir -p "$AVD_DIR"

printf 'avd.ini.encoding=UTF-8\npath=%s\ntarget=%s\n' "$AVD_DIR" "$API_ID" > "$AVD_INI"

CONFIG=$AVD_DIR/config.ini
touch "$CONFIG"
# Replaces key=value in config.ini, or appends it, keeping everything else.
set_ini() {
    local key=$1 value=$2 tmp
    tmp=$(mktemp)
    awk -v k="$key" -v v="$value" -F= '
        { split($0, a, "="); gsub(/^[ \t]+|[ \t]+$/, "", a[1]) }
        a[1] == k && !done { print k "=" v; done = 1; next }
        { print }
        END { if (!done) print k "=" v }' "$CONFIG" > "$tmp"
    mv "$tmp" "$CONFIG"
}

case "$IMAGE_TYPE" in
    google_apis_playstore) TAG_DISPLAY="Google Play"; PLAY_STORE=true ;;
    google_apis) TAG_DISPLAY="Google APIs"; PLAY_STORE=false ;;
    *) TAG_DISPLAY="Default Android System Image"; PLAY_STORE=false ;;
esac
ORIENTATION=$([[ $LCD_WIDTH -gt $LCD_HEIGHT ]] && echo landscape || echo portrait)

while IFS='=' read -r key value; do
    [[ -n $key ]] && set_ini "$key" "$value"
done <<EOF
AvdId=$AVD_NAME
avd.ini.displayname=$AVD_NAME
avd.ini.encoding=UTF-8
target=$API_ID
image.sysdir.1=system-images/$API_ID/$IMAGE_TYPE/x86_64/
tag.id=$IMAGE_TYPE
tag.display=$TAG_DISPLAY
abi.type=x86_64
hw.cpu.arch=x86_64
PlayStore.enabled=$PLAY_STORE
hw.cpu.ncore=$CORES
hw.ramSize=$RAM_MB
vm.heapSize=512
disk.dataPartition.size=${DATA_GB}G
hw.sdCard=no
hw.gpu.enabled=yes
hw.gpu.mode=host
hw.lcd.width=$LCD_WIDTH
hw.lcd.height=$LCD_HEIGHT
hw.lcd.density=$LCD_DENSITY
hw.initialOrientation=$ORIENTATION
showDeviceFrame=no
hw.keyboard=yes
hw.mainKeys=no
hw.dPad=no
hw.trackBall=no
hw.accelerometer=yes
hw.gyroscope=yes
hw.sensors.orientation=yes
hw.sensors.proximity=yes
hw.gps=yes
hw.battery=yes
hw.audioInput=no
hw.audioOutput=yes
hw.camera.back=emulated
hw.camera.front=emulated
runtime.network.latency=none
runtime.network.speed=full
fastboot.forceColdBoot=no
fastboot.forceFastBoot=yes
EOF

# Where Resit finds the SDK: next to this script (repo checkout) and in its settings folder
# (an installed copy).
STATE=$(printf '{\n  "avdName": "%s",\n  "sdkRoot": "%s",\n  "avdHome": "%s",\n  "image": "system-images/%s/%s/x86_64",\n  "cores": %s,\n  "ramMB": %s\n}\n' \
    "$AVD_NAME" "$SDK_ROOT" "$AVD_HOME" "$API_ID" "$IMAGE_TYPE" "$CORES" "$RAM_MB")
echo "$STATE" > "$SCRIPT_DIR/resit.local.json" 2>/dev/null || true
mkdir -p "$CONFIG_DIR"
echo "$STATE" > "$CONFIG_DIR/resit.local.json"

echo
printf '\033[32mDone. Start Resit and press Play on "%s".\033[0m\n' "$AVD_NAME"
[[ $ACCEL_OK -eq 1 ]] || printf '\033[33mBut fix KVM first (see above), or the emulator will crawl.\033[0m\n'
