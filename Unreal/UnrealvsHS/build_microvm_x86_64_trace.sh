#!/usr/bin/env bash

set -e

# =============================================================================
# TRACING variant of build_microvm_x86_64.sh — x86_64 only.
#
# Differences vs the plain build_microvm_x86_64.sh:
#   1. Refuses Shipping builds. Unreal compiles out almost all CPU trace scopes
#      (TRACE_CPUPROFILER_EVENT_SCOPE) in Shipping, so the .utrace opens EMPTY.
#      This script requires a *Test* (preferred) or *Development* server build.
#   2. Boots the server with the loopback smoke test (-SimulatedClients=N) so it
#      self-drives rounds 1->10 with no network client.
#   3. Enables Unreal Insights tracing and STREAMS it live to a running
#      UnrealInsights.exe on your workstation (-tracehost). The initramfs rootfs
#      is tmpfs, so a -tracefile would die with the VM — streaming avoids that.
#      This VM is bridged onto the LAN (static IP), so it can reach the store.
#
# BEFORE booting the VM:
#   Start UnrealInsights.exe on the workstation FIRST. Its trace store listens
#   on TCP 1980. When the VM boots, a live session appears in Insights.
#
# Prerequisites on the build host:
#   - docker with buildx (same-arch x86_64 builds run native)
#   - qemu-system-x86_64 + cpio + gzip + curl + grub-mkrescue (xorriso, mtools)
#   - A Linux Dedicated Server build in **Test** or **Development** config
#     (UE5 Editor -> Platforms -> Linux -> set config to Development/Test ->
#      Cook Content + Package Server). NOT Shipping.
#
# Usage:
#   ./build_microvm_x86_64_trace.sh                      # auto-locate newest Test/Dev build
#   ./build_microvm_x86_64_trace.sh /path/to/StagedBuild # explicit staged-build root
#
# Tunables (env at build time):
#   TRACE_HOST=192.168.0.207   # workstation running UnrealInsights.exe (store :1980)
#   TRACE_CHANNELS=cpu,frame,bookmark,counters,task
#   SIM_CLIENTS=1              # loopback sweep-fire bots (0..4)
#   STATIC_IP / STATIC_GATEWAY / STATIC_CIDR / STATIC_DNS (as in the plain script)
# =============================================================================

UNREAL_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$UNREAL_PROJECT_ROOT"

# ---- Tracing / smoke-test configuration -------------------------------------
# TRACE_PORT is the UnrealTraceServer *recorder* port (where producers push), NOT
# the store port. It is version-dependent: check UnrealInsights -> Trace Store tab
# -> hover the store-host status ("Recorder port: N"). Recent builds use 1981;
# older builds used 1980. Set TRACE_PORT to whatever that tooltip shows.
TRACE_HOST="${TRACE_HOST:-192.168.0.207}"          # workstation with UnrealInsights.exe
TRACE_PORT="${TRACE_PORT:-1981}"                   # recorder port (Trace Store tab tooltip)
TRACE_CHANNELS="${TRACE_CHANNELS:-cpu,frame,bookmark,counters,task}"
SIM_CLIENTS="${SIM_CLIENTS:-1}"
echo "==> Trace target (recorder):             ${TRACE_HOST}:${TRACE_PORT}"
echo "==> Trace channels:                      ${TRACE_CHANNELS}"
echo "==> Simulated clients (smoke test):      ${SIM_CLIENTS}"

# Static IP configuration baked into init.sh. Override at build time, e.g.:
#   STATIC_IP=192.168.1.50 STATIC_GATEWAY=192.168.1.1 ./build_microvm_x86_64_trace.sh
STATIC_IP="${STATIC_IP:-192.168.0.205}"
STATIC_CIDR="${STATIC_CIDR:-24}"
STATIC_GATEWAY="${STATIC_GATEWAY:-192.168.0.1}"
STATIC_DNS="${STATIC_DNS:-8.8.8.8}"
echo "==> Static IP for this build: ${STATIC_IP}/${STATIC_CIDR} gw=${STATIC_GATEWAY} dns=${STATIC_DNS}"

BUILD_DIR="${1:-}"
if [ -z "$BUILD_DIR" ]; then
    # TRACE build: only Test or Development binaries carry CPU trace instrumentation,
    # so NEVER auto-pick a Shipping (or Debug) tree. Prefer Test, then Development
    # (unsuffixed). This avoids grabbing e.g. .../X86/.../Server-Linux-Shipping just
    # because it sorts last. Pass a staged-build dir explicitly to override.
    BIN_PATH=$(find ../../Build -maxdepth 10 -type f \
               -regex '.*/Binaries/Linux/[^/]*Server-Linux-Test' 2>/dev/null | sort | tail -1)
    if [ -z "$BIN_PATH" ]; then
        # Development binaries are unsuffixed; exclude Shipping/Test/Debug suffixes.
        BIN_PATH=$(find ../../Build -maxdepth 10 -type f \
                   -regex '.*/Binaries/Linux/[^/]*Server' 2>/dev/null | sort | tail -1)
    fi
    if [ -n "$BIN_PATH" ]; then
        BUILD_DIR=$(cd "$(dirname "$BIN_PATH")/../../.." && pwd)
    fi
fi

if [ -z "$BUILD_DIR" ] || [ ! -d "$BUILD_DIR" ]; then
    cat >&2 <<EOF
ERROR: cannot find an Unreal Linux x86_64 server staged build.

Pass the staged-build directory explicitly:
    $0 /absolute/path/to/Build/<date>/UnrealServer/LinuxServer

Build profile must target:
    Platform = Linux
    Build    = Server
    Config   = Development or Test  (NOT Shipping — trace is compiled out)
    Output must contain <Project>Server.sh and
    <Project>/Binaries/Linux/<Project>Server[-Linux-Test].
EOF
    exit 1
fi

BUILD_DIR=$(cd "$BUILD_DIR" && pwd)

# Pick the launcher (top-level <Project>Server.sh) and derive project / binary names.
LAUNCHER=$(find "$BUILD_DIR" -maxdepth 1 -type f -name "*Server.sh" | head -1)
if [ -z "$LAUNCHER" ]; then
    echo "ERROR: no <Project>Server.sh launcher in $BUILD_DIR" >&2
    exit 1
fi
LAUNCHER_NAME=$(basename "$LAUNCHER")               # UnrealvsHSServer.sh
PROJECT_BINARY="${LAUNCHER_NAME%.sh}"               # UnrealvsHSServer
PROJECT_NAME="${PROJECT_BINARY%Server}"             # UnrealvsHS

# Resolve the on-disk binary, preferring configs that KEEP trace instrumentation.
# UE naming: Development = unsuffixed; Test/Shipping/Debug get a -Linux-<Config> suffix.
# Prefer Test, then Development. Reject Shipping (trace scopes stripped).
BINDIR="$BUILD_DIR/$PROJECT_NAME/Binaries/Linux"
BIN_FULL_PATH=""
for cand in "${PROJECT_BINARY}-Linux-Test" "${PROJECT_BINARY}"; do
    if [ -f "$BINDIR/$cand" ]; then
        BIN_FULL_PATH="$BINDIR/$cand"
        break
    fi
done

if [ -z "$BIN_FULL_PATH" ]; then
    SHIP=$(find "$BINDIR" -maxdepth 1 -type f -name "${PROJECT_BINARY}-Linux-Shipping" 2>/dev/null | head -1)
    if [ -n "$SHIP" ]; then
        cat >&2 <<EOF
ERROR: only a SHIPPING server binary was found:
    $SHIP

Shipping strips CPU trace scopes — the .utrace would open empty (your exact
symptom). Rebuild the server target in **Test** (recommended for representative
timings) or **Development**, then re-run this script.
EOF
    else
        echo "ERROR: no Test/Development server binary found under $BINDIR/" >&2
        echo "       Expected ${PROJECT_BINARY}-Linux-Test or ${PROJECT_BINARY} (Development)." >&2
    fi
    exit 1
fi
PROJECT_BINARY=$(basename "$BIN_FULL_PATH")         # real on-disk name, suffix included

echo "==> Using Unreal staged build at: $BUILD_DIR"
echo "==> Project:                     $PROJECT_NAME"
echo "==> Launcher:                    $LAUNCHER_NAME"
echo "==> Server binary:               $PROJECT_NAME/Binaries/Linux/$PROJECT_BINARY"
case "$PROJECT_BINARY" in
    *-Linux-Test) echo "==> Config:                      Test (trace OK)";;
    *-Linux-*)    echo "==> Config:                      $PROJECT_BINARY (unexpected — proceeding)";;
    *)            echo "==> Config:                      Development (trace OK)";;
esac

mkdir -p .microvm_x86_64
cd .microvm_x86_64

APK_MIRROR="https://dl-cdn.alpinelinux.org/alpine/latest-stable/main/x86_64/"

if [ ! -f "vmlinuz-virt" ] || [ ! -d "modules" ]; then
    echo "==> Discovering latest linux-virt apk from $APK_MIRROR ..."
    APK_NAME=$(curl -fsSL "$APK_MIRROR" \
               | grep -oE 'linux-virt-[0-9.]+-r[0-9]+\.apk' \
               | sort -V | tail -1)
    if [ -z "$APK_NAME" ]; then
        echo "ERROR: could not find linux-virt apk in $APK_MIRROR" >&2
        echo "       (mirror layout changed, or curl/grep is broken)" >&2
        exit 1
    fi
    echo "==> Selected: $APK_NAME"
    echo "==> Fetching kernel + virtio_net modules..."
    curl -fsSL -O "$APK_MIRROR$APK_NAME"
    mkdir -p apk_extract modules
    tar -xzf "$APK_NAME" -C apk_extract
    cp apk_extract/boot/vmlinuz-virt vmlinuz-virt
    # Module dir name (e.g. 6.18.35-0-virt) is whatever the apk contains.
    KERNEL_MOD_DIR=$(ls apk_extract/lib/modules/ | head -1)
    if [ -z "$KERNEL_MOD_DIR" ]; then
        echo "ERROR: no module directory inside extracted apk" >&2
        exit 1
    fi
    echo "==> Module dir: $KERNEL_MOD_DIR"
    for m in failover net_failover virtio_net; do
        src=$(find "apk_extract/lib/modules/${KERNEL_MOD_DIR}" -name "${m}.ko.gz" | head -1)
        if [ -z "$src" ]; then
            echo "ERROR: ${m}.ko.gz not found in extracted apk" >&2
            exit 1
        fi
        gunzip -c "$src" > "modules/${m}.ko"
    done
    rm -rf apk_extract "$APK_NAME"
else
    echo "==> Kernel + modules already extracted. Skipping."
fi

# Stage the build, skipping the ~1.4 GB of *.debug / *.sym / *.target debug symbols.
echo "==> Staging Unreal build (excluding *.debug, *.sym, *.target)..."
rm -rf unreal_build
mkdir -p unreal_build
( cd "$BUILD_DIR" && tar -cf - --exclude='*.debug' --exclude='*.sym' --exclude='*.target' . ) \
    | ( cd unreal_build && tar -xf - )

cat > Dockerfile.microvm <<DOCKEREOF
FROM --platform=linux/amd64 debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \\
        libstdc++6 libgcc-s1 ca-certificates \\
        kmod iproute2 udhcpc coreutils procps less findutils \\
        libcurl4 libssl3 libgssapi-krb5-2 libnuma1 \\
        libxrandr2 libxcursor1 libxext6 libxi6 libxinerama1 libxss1 \\
        libasound2 libdbus-1-3 libnss3 libuuid1 libgl1 libegl1 \\
    && rm -rf /var/lib/apt/lists/*

COPY unreal_build/ /opt/app/
RUN chmod +x "/opt/app/${LAUNCHER_NAME}" && \\
    chmod +x "/opt/app/${PROJECT_NAME}/Binaries/Linux/${PROJECT_BINARY}"

# UE5 refuses to run as root. Create an unprivileged user and give it ownership.
RUN useradd -u 1000 -m -s /bin/sh unreal && \\
    chown -R unreal:unreal /opt/app

COPY init.sh /init
RUN chmod +x /init
DOCKEREOF

cat > init.sh <<INITEOF
#!/bin/sh

# Mount /dev first so /dev/console exists, then bind our I/O to it explicitly.
mount -t devtmpfs devtmpfs /dev 2>/dev/null
mount -t proc proc /proc 2>/dev/null
mount -t sysfs sysfs /sys 2>/dev/null
mount -t tmpfs tmpfs /tmp 2>/dev/null

exec >/dev/console 2>&1

log() {
    msg="[init] \$*"
    echo "\$msg"
    echo "\$msg" > /dev/kmsg 2>/dev/null
}

log "starting (PID \$\$)"
log "all four basic mounts done"

# UE writes user config to \$HOME/.config/Epic/... — point it at tmpfs.
export HOME=/tmp
export TMPDIR=/tmp
log "env set: HOME=\$HOME TMPDIR=\$TMPDIR"

for m in failover net_failover virtio_net; do
    insmod /lib/modules/\${m}.ko 2>/dev/null
    log "insmod \${m}"
done

ip link set lo up   && log "lo up"

# Static IP — baked in at build time. Override with STATIC_IP=... at build invocation.
ip addr add ${STATIC_IP}/${STATIC_CIDR} dev eth0 && log "ip addr add ${STATIC_IP}/${STATIC_CIDR}"
ip link set eth0 up && log "eth0 up"
ip route add default via ${STATIC_GATEWAY} 2>/dev/null && log "default route via ${STATIC_GATEWAY}" || log "WARN: failed to add default route via ${STATIC_GATEWAY}"
echo "nameserver ${STATIC_DNS}" > /etc/resolv.conf && log "dns: ${STATIC_DNS}"
log "ip addr now: \$(ip -4 -o addr show eth0 | awk '{print \$4}')"

# Sanity: can we reach the Unreal Insights trace store host?
if ping -c1 -W2 ${TRACE_HOST} >/dev/null 2>&1; then
    log "trace host ${TRACE_HOST} reachable"
else
    log "WARN: trace host ${TRACE_HOST} NOT reachable — start UnrealInsights.exe there (store :1980) or set TRACE_HOST"
fi

log "===== diag block start ====="
log "kernel: \$(uname -a)"
log "/opt/app entries:"
ls -1 /opt/app | while read f; do log "  \$f"; done
log "checking launcher at /opt/app/${LAUNCHER_NAME}"
if [ -f "/opt/app/${LAUNCHER_NAME}" ]; then
    log "launcher exists, size: \$(stat -c%s /opt/app/${LAUNCHER_NAME} 2>/dev/null) bytes"
else
    log "WARN: launcher NOT FOUND at /opt/app/${LAUNCHER_NAME}"
fi
log "checking server binary at /opt/app/${PROJECT_NAME}/Binaries/Linux/${PROJECT_BINARY}"
if [ -f "/opt/app/${PROJECT_NAME}/Binaries/Linux/${PROJECT_BINARY}" ]; then
    log "server binary exists, size: \$(stat -c%s /opt/app/${PROJECT_NAME}/Binaries/Linux/${PROJECT_BINARY} 2>/dev/null) bytes"
else
    log "WARN: server binary NOT FOUND"
fi
log "checking server binary dynamic deps"
MISSING=\$(ldd /opt/app/${PROJECT_NAME}/Binaries/Linux/${PROJECT_BINARY} 2>&1 | grep 'not found' || true)
if [ -n "\$MISSING" ]; then
    log "WARN: server binary has missing libs:"
    echo "\$MISSING" | while read line; do log "  \$line"; done
else
    log "server binary: all deps resolved"
fi
log "===== diag block end ====="
log "launching Unreal (TRACE build): -SimulatedClients=${SIM_CLIENTS} -trace=${TRACE_CHANNELS} -tracehost=${TRACE_HOST}:${TRACE_PORT}"
log "Live trace streams to the UnrealTraceServer recorder on ${TRACE_HOST}:${TRACE_PORT} — start UnrealInsights.exe there FIRST."
log "Unreal output -> /tmp/unreal.log (tail -f /tmp/unreal.log from shell to watch)"

cd /opt/app
# Drop root before exec — UE5 refuses to run as root. -allowroot also passed as belt-and-suspenders.
# Trace flags:
#   -SimulatedClients=N  loopback sweep-fire bots (self-driving smoke test, no network)
#   -trace=<channels>    Unreal Insights channels
#   -statnamedevents     keep named CPU scopes visible in the Timers view
#   -tracehost=IP        stream live to the UnrealInsights store on that host (TCP 1980)
su unreal -c "cd /opt/app && ./${LAUNCHER_NAME} -QuicPort=7780 -SimulatedClients=${SIM_CLIENTS} -log -allowroot -nullrhi -nosound -unattended -trace=${TRACE_CHANNELS} -statnamedevents -tracehost=${TRACE_HOST}:${TRACE_PORT}" > /tmp/unreal.log 2>&1 &
UNREAL_PID=\$!
log "Unreal launched as PID \$UNREAL_PID"

setsid /bin/sh -i </dev/console >/dev/console 2>&1 &
SHELL_PID=\$!
log "diagnostic shell PID \$SHELL_PID — press Enter for prompt"

# Poll Unreal in the background; surface its exit code via kmsg so we see it on every console.
(
    wait \$UNREAL_PID
    UEXIT=\$?
    echo "[init] Unreal exited code \$UEXIT — see /tmp/unreal.log for output" > /dev/kmsg
) &

# PID 1 must never exit. The setsid shell above handles interactive console; we just sleep forever.
while true; do
    sleep 3600
done
INITEOF

echo "==> Building x86_64 rootfs via Docker buildx..."
docker buildx build --platform linux/amd64 --load \
    -t dgsvshs-unreal-microvm-x64-trace:latest \
    -f Dockerfile.microvm .

echo "==> Exporting rootfs..."
CID=$(docker create --platform linux/amd64 dgsvshs-unreal-microvm-x64-trace:latest)
rm -rf rootfs_dir
mkdir rootfs_dir
docker export "$CID" | tar -x -C rootfs_dir
docker rm "$CID" >/dev/null

mkdir -p rootfs_dir/lib/modules
cp modules/*.ko rootfs_dir/lib/modules/

echo "==> Packaging initramfs (this can take a while — Unreal build is large)..."
cd rootfs_dir
find . | cpio -H newc -o 2>/dev/null | gzip -9 > ../initramfs.cpio.gz
cd ..

echo "==> Packaging bootable ISO..."
command -v grub-mkrescue >/dev/null || {
    cat >&2 <<EOF
ERROR: grub-mkrescue not found. Install ISO build tools:
    sudo apt install -y grub-pc-bin grub-efi-amd64-bin xorriso mtools
EOF
    exit 1
}

rm -rf iso_staging
mkdir -p iso_staging/boot/grub
cp vmlinuz-virt        iso_staging/boot/vmlinuz
cp initramfs.cpio.gz   iso_staging/boot/initrd.img

cat > iso_staging/boot/grub/grub.cfg <<'GRUBEOF'
set timeout=2
set default=0
menuentry "DGSvsHS Unreal MicroVM (TRACE)" {
    linux  /boot/vmlinuz console=tty0 console=ttyS0 cgroup_disable=memory,pids
    initrd /boot/initrd.img
}
GRUBEOF

grub-mkrescue -o unreal-microvm-trace.iso iso_staging >/dev/null 2>&1
rm -rf iso_staging

echo "==> ISO ready: $(pwd)/unreal-microvm-trace.iso"
echo
echo "    To capture a trace:"
echo "      1. On ${TRACE_HOST}: launch UnrealInsights.exe. In the Trace Store tab, hover the"
echo "         store-host status and CONFIRM the 'Recorder port' matches TRACE_PORT=${TRACE_PORT}."
echo "         If it differs, rebuild with TRACE_PORT=<that port> ./build_microvm_x86_64_trace.sh"
echo "      2. Open inbound TCP ${TRACE_PORT} in the Windows firewall on ${TRACE_HOST}:"
echo "         New-NetFirewallRule -DisplayName 'UnrealTrace ${TRACE_PORT}' -Direction Inbound -Protocol TCP -LocalPort ${TRACE_PORT} -Action Allow"
echo "      3. Upload this ISO via Proxmox UI > Storage > ISO Images > Upload."
echo "      4. Create VM, set CD/DVD to this ISO, start it."
echo "      5. A live session for ${STATIC_IP} appears in the Trace Store list / Session Frontend."
echo
echo "    If no session appears: check the VM console for the 'trace host ... reachable' line,"
echo "    confirm the recorder port (${TRACE_PORT}) is correct and open on ${TRACE_HOST}, and note"
echo "    that UnrealTraceServer must accept remote producers (it binds the recorder on all"
echo "    interfaces by default; the '127.0.0.1' shown in the UI is only how the frontend reaches"
echo "    its local store)."
