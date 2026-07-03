#!/usr/bin/env bash

set -e

# Prerequisites on the build host:
#   - docker with buildx (no binfmt needed — same-arch x86_64 builds run native)
#   - qemu-system-x86_64 + cpio + gzip + curl
#   - A Unity Linux Dedicated Server build for the x86_64 architecture
#     (Build Settings → Dedicated Server → Linux → Architecture: x86_64)
#
# Usage:
#   ./build_microvm_x86_64.sh                         # auto-locate most recent x86_64 build
#   ./build_microvm_x86_64.sh /path/to/Unity/Server   # explicit build dir
#
# The build dir must contain the entrypoint binary, UnityPlayer.so, and a *_Data/ folder.

UNITY_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$UNITY_PROJECT_ROOT"

# Static IP configuration baked into init.sh. Override at build time, e.g.:
#   STATIC_IP=192.168.1.50 STATIC_GATEWAY=192.168.1.1 ./build_microvm_x86_64.sh
STATIC_IP="${STATIC_IP:-192.168.0.205}"
STATIC_CIDR="${STATIC_CIDR:-24}"
STATIC_GATEWAY="${STATIC_GATEWAY:-192.168.0.1}"
STATIC_DNS="${STATIC_DNS:-8.8.8.8}"
echo "==> Static IP for this build: ${STATIC_IP}/${STATIC_CIDR} gw=${STATIC_GATEWAY} dns=${STATIC_DNS}"

BUILD_DIR="${1:-}"
if [ -z "$BUILD_DIR" ]; then
    # Look for the most recent dated build dir containing a Linux Server tree.
    # Heuristic: directory that contains UnityPlayer.so (the binary may be named
    # after the project, the build profile, or have a .x86_64 suffix).
    BUILD_DIR=$(find ../Build -mindepth 2 -maxdepth 5 -type f -name "UnityPlayer.so" 2>/dev/null \
                | while read f; do dirname "$f"; done \
                | sort | tail -1)
fi

if [ -z "$BUILD_DIR" ] || [ ! -f "$BUILD_DIR/UnityPlayer.so" ]; then
    cat >&2 <<EOF
ERROR: cannot find a Unity Linux x86_64 server build.

Pass the build directory explicitly:
    $0 /absolute/path/to/Build/<date>/Server/LinuxServerx86_64

Build profile must target:
    Platform     = Dedicated Server
    Target       = Linux
    Architecture = x86_64
    Output dir must contain the entrypoint binary, UnityPlayer.so, and a *_Data/ folder.
EOF
    exit 1
fi

DATA_DIR=$(find "$BUILD_DIR" -maxdepth 1 -type d -name "*_Data" | head -1)
if [ -z "$DATA_DIR" ]; then
    echo "ERROR: no *_Data folder found in $BUILD_DIR" >&2
    exit 1
fi
DATA_BASENAME=$(basename "$DATA_DIR")
BINARY_BASENAME="${DATA_BASENAME%_Data}"
if   [ -f "$BUILD_DIR/$BINARY_BASENAME" ];         then BINARY_FILE="$BINARY_BASENAME"
elif [ -f "$BUILD_DIR/$BINARY_BASENAME.x86_64" ];  then BINARY_FILE="$BINARY_BASENAME.x86_64"
else
    echo "ERROR: no entrypoint binary matching $BINARY_BASENAME[.x86_64] in $BUILD_DIR" >&2
    exit 1
fi

BUILD_DIR=$(cd "$BUILD_DIR" && pwd)

echo "==> Using Unity build at: $BUILD_DIR"
echo "==> Entrypoint binary:    $BINARY_FILE"

mkdir -p .microvm_x86_64
cd .microvm_x86_64

KERNEL_VERSION="6.18.35-r0"
KERNEL_MOD_DIR="6.18.35-0-virt"

if [ ! -f "vmlinuz-virt" ] || [ ! -d "modules" ]; then
    echo "==> Fetching kernel + virtio_net modules from Alpine apk..."
    curl -sLO "https://dl-cdn.alpinelinux.org/alpine/latest-stable/main/x86_64/linux-virt-${KERNEL_VERSION}.apk"
    mkdir -p apk_extract modules
    tar -xzf "linux-virt-${KERNEL_VERSION}.apk" -C apk_extract 2>/dev/null
    cp apk_extract/boot/vmlinuz-virt vmlinuz-virt
    for m in failover net_failover virtio_net; do
        src=$(find "apk_extract/lib/modules/${KERNEL_MOD_DIR}" -name "${m}.ko.gz")
        gunzip -c "$src" > "modules/${m}.ko"
    done
    rm -rf apk_extract "linux-virt-${KERNEL_VERSION}.apk"
else
    echo "==> Kernel + modules already extracted. Skipping."
fi

rm -rf unity_build
cp -a "$BUILD_DIR" unity_build

# The DGS server now P/Invokes the native QUIC socket (libdgsvshs_socket.so). Unity
# doesn't reliably bundle it into *_Data/Plugins for Linux, so drop it next to the
# binary; init.sh adds /opt/app to LD_LIBRARY_PATH so dlopen always resolves it.
# Build the .so for Debian bookworm glibc (see the header note) into Assets/Plugins.
SOCKET_SO="../Assets/Plugins/x86_64/libdgsvshs_socket.so"
if [ -f "$SOCKET_SO" ]; then
    # Stage under both names — IL2CPP's Linux resolver tried the raw "dgsvshs_socket"
    # (see the DllNotFound error); provide lib-prefixed too so either lookup resolves.
    cp "$SOCKET_SO" unity_build/libdgsvshs_socket.so
    cp "$SOCKET_SO" unity_build/dgsvshs_socket.so
    echo "==> Bundled libdgsvshs_socket.so (+ dgsvshs_socket.so) next to the Unity binary."
else
    echo "WARN: $SOCKET_SO not found — the DGS QUIC server will DllNotFoundException."
    echo "      Build it (Debian-bookworm glibc) and place it there:"
    echo "        docker run --rm -v \"\$(pwd)/native/quic_client:/src\" -w /src rust:1-bookworm \\"
    echo "          cargo build --release --target-dir /src/target-linux"
    echo "        cp native/quic_client/target-linux/release/libdgsvshs_socket.so \\"
    echo "           DGSvsHS/Assets/Plugins/x86_64/"
fi

cat > Dockerfile.microvm <<DOCKEREOF
FROM --platform=linux/amd64 debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \\
        libstdc++6 libgcc-s1 ca-certificates \\
        kmod iproute2 udhcpc coreutils procps less \\
        libcurl4 libssl3 libgssapi-krb5-2 \\
        libxrandr2 libxcursor1 libxext6 libxi6 libxinerama1 libxss1 \\
        libasound2 libdbus-1-3 libnss3 libuuid1 libgl1 libegl1 \\
    && rm -rf /var/lib/apt/lists/*

COPY unity_build/ /opt/app/
RUN chmod +x "/opt/app/${BINARY_FILE}"

COPY init.sh /init
RUN chmod +x /init
DOCKEREOF

cat > init.sh <<INITEOF
#!/usr/bin/dash

# Mount /dev first so /dev/console exists, then bind our I/O to it explicitly.
# This works around kernel attaching init's fds to a non-visible tty.
mount -t devtmpfs devtmpfs /dev 2>/dev/null
mount -t proc proc /proc 2>/dev/null
mount -t sysfs sysfs /sys 2>/dev/null
mount -t tmpfs tmpfs /tmp 2>/dev/null

exec >/dev/console 2>&1

# Log via stdout (now on /dev/console) AND /dev/kmsg (kernel log, visible via dmesg
# and on every console the kernel was told about). Belt and suspenders.
log() {
    msg="[init] \$*"
    echo "\$msg"
    echo "\$msg" > /dev/kmsg 2>/dev/null
}

log "starting (PID \$\$)"
log "all four basic mounts done"

# Unity writes player log to \$HOME/.config/unity3d/... by default — point it at tmpfs.
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
log "===== diag block start ====="
log "kernel: \$(uname -a)"
log "/opt/app entries:"
ls -1 /opt/app | while read f; do log "  \$f"; done
log "checking entrypoint binary at /opt/app/${BINARY_FILE}"
if [ -f "/opt/app/${BINARY_FILE}" ]; then
    log "binary exists, size: \$(stat -c%s /opt/app/${BINARY_FILE} 2>/dev/null) bytes"
else
    log "WARN: binary NOT FOUND at /opt/app/${BINARY_FILE}"
fi
log "checking UnityPlayer.so"
if [ -f /opt/app/UnityPlayer.so ]; then
    log "UnityPlayer.so exists, size: \$(stat -c%s /opt/app/UnityPlayer.so 2>/dev/null) bytes"
else
    log "WARN: UnityPlayer.so NOT FOUND"
fi
log "checking UnityPlayer.so dynamic deps"
MISSING=\$(ldd /opt/app/UnityPlayer.so 2>&1 | grep 'not found' || true)
if [ -n "\$MISSING" ]; then
    log "WARN: UnityPlayer.so has missing libs:"
    echo "\$MISSING" | while read line; do log "  \$line"; done
else
    log "UnityPlayer.so: all deps resolved"
fi
log "===== diag block end ====="
log "launching Unity: ./${BINARY_FILE} -batchmode -nographics"
log "Unity output -> /tmp/unity.log (tail -f /tmp/unity.log from shell to watch)"

cd /opt/app
# Resolve the native QUIC socket (libdgsvshs_socket.so) staged next to the binary.
export LD_LIBRARY_PATH="/opt/app:\$LD_LIBRARY_PATH"
"./${BINARY_FILE}" -batchmode -nographics -logFile - > /tmp/unity.log 2>&1 &
UNITY_PID=\$!
log "Unity launched as PID \$UNITY_PID"

setsid /usr/bin/dash -i </dev/console >/dev/console 2>&1 &
SHELL_PID=\$!
log "diagnostic shell PID \$SHELL_PID — press Enter for prompt"

# Poll Unity in the background; surface its exit code via kmsg so we see it on every console.
(
    wait \$UNITY_PID
    UEXIT=\$?
    echo "[init] Unity exited code \$UEXIT — see /tmp/unity.log for output" > /dev/kmsg
) &

# PID 1 must never exit. The setsid shell above handles interactive console; we just sleep forever.
while true; do
    sleep 3600
done
INITEOF

echo "==> Building x86_64 rootfs via Docker buildx..."
docker buildx build --platform linux/amd64 --load \
    -t dgsvshs-unity-microvm-x64:latest \
    -f Dockerfile.microvm .

echo "==> Exporting rootfs..."
CID=$(docker create --platform linux/amd64 dgsvshs-unity-microvm-x64:latest)
rm -rf rootfs_dir
mkdir rootfs_dir
docker export "$CID" | tar -x -C rootfs_dir
docker rm "$CID" >/dev/null

mkdir -p rootfs_dir/lib/modules
cp modules/*.ko rootfs_dir/lib/modules/

echo "==> Packaging initramfs (this can take a while — Unity build is large)..."
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
menuentry "DGSvsHS Unity DGS MicroVM" {
    linux  /boot/vmlinuz console=tty0 console=ttyS0 cgroup_disable=memory,pids
    initrd /boot/initrd.img
}
GRUBEOF

grub-mkrescue -o dgs-microvm.iso iso_staging >/dev/null 2>&1
rm -rf iso_staging

echo "==> ISO ready: $(pwd)/dgs-microvm.iso"
echo "    Upload via Proxmox UI > Storage > ISO Images > Upload."
echo "    Create VM, set CD/DVD to this ISO, start. Logs in the Console tab."
