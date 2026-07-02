#!/usr/bin/env bash

set -e

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$PROJECT_ROOT"

# Prerequisites on the build host:
#   - rustup target add aarch64-unknown-linux-musl  +  cargo install cargo-zigbuild  +  zig
#   - docker with buildx + binfmt_misc (cross-arch image build)
#   - qemu-system-aarch64 + cpio + gzip + curl
# On Apple Silicon: -accel=hvf works natively. On Linux: change to kvm. On WSL: tcg (slow).
#
# Usage:
#   ./build_microvm_aarch64.sh             # normal build
#   ./build_microvm_aarch64.sh --god-mode  # build with --god-mode CLI flag baked in
#                                          # (init.sh launches `./dgsvshs-bevy --god-mode`)

god_mode=0
for arg in "$@"; do
    case "$arg" in
        --god-mode) god_mode=1 ;;
        *) echo "[build] unknown arg: $arg" >&2; exit 2 ;;
    esac
done
flavor_suffix=""
launch_args=""
if [[ $god_mode -eq 1 ]]; then
    flavor_suffix="-godmode"
    launch_args="--god-mode"
fi
echo "==> Flavor: ${flavor_suffix:-normal}"

# Static IP configuration baked into init.sh. Override at build time, e.g.:
#   STATIC_IP=192.168.1.50 STATIC_GATEWAY=192.168.1.1 ./build_microvm_aarch64.sh
STATIC_IP="${STATIC_IP:-192.168.0.205}"
STATIC_CIDR="${STATIC_CIDR:-24}"
STATIC_GATEWAY="${STATIC_GATEWAY:-192.168.0.1}"
STATIC_DNS="${STATIC_DNS:-8.8.8.8}"
echo "==> Static IP for this build: ${STATIC_IP}/${STATIC_CIDR} gw=${STATIC_GATEWAY} dns=${STATIC_DNS}"

echo "==> Compiling Bevy Server..."
cargo zigbuild --target aarch64-unknown-linux-musl --release

mkdir -p .microvm_aarch64
cd .microvm_aarch64

KERNEL_VERSION="6.18.35-r0"
KERNEL_MOD_DIR="6.18.35-0-virt"

if [ ! -f "vmlinuz-virt" ] || [ ! -d "modules" ]; then
    echo "==> Fetching kernel + virtio_net modules from Alpine apk..."
    curl -sLO "https://dl-cdn.alpinelinux.org/alpine/latest-stable/main/aarch64/linux-virt-${KERNEL_VERSION}.apk"
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

# Stage the freshly-built static-musl binary for the Dockerfile COPY.
rm -rf publish
mkdir -p publish
cp "../target/aarch64-unknown-linux-musl/release/cli" publish/dgsvshs-bevy
chmod +x publish/dgsvshs-bevy

cat > Dockerfile.microvm <<'DOCKEREOF'
FROM --platform=linux/arm64 alpine:3.21

# Rust binary is fully static-musl; ca-certificates for any TLS outbound,
# procps/less for the diagnostic shell, iproute2 for full-featured `ip`
# (busybox `ip addr add` races eth0 enumeration and silently no-ops).
RUN apk add --no-cache ca-certificates procps less iproute2

COPY publish/dgsvshs-bevy /opt/app/dgsvshs-bevy
RUN chmod +x /opt/app/dgsvshs-bevy

COPY init.sh /init
RUN chmod +x /init
DOCKEREOF

cat > init.sh <<'INITEOF_TOP'
#!/bin/sh

# Mount /dev first so /dev/console exists, then bind I/O to it explicitly.
mount -t devtmpfs devtmpfs /dev 2>/dev/null
mount -t proc proc /proc 2>/dev/null
mount -t sysfs sysfs /sys 2>/dev/null
mount -t tmpfs tmpfs /tmp 2>/dev/null

exec >/dev/console 2>&1

log() {
    msg="[init] $*"
    echo "$msg"
    echo "$msg" > /dev/kmsg 2>/dev/null
}

log "starting (PID $$)"

for m in failover net_failover virtio_net; do
    insmod /lib/modules/${m}.ko 2>/dev/null
    log "insmod ${m}"
done

ip link set lo up && log "lo up"
INITEOF_TOP

cat >> init.sh <<NETEOF
ip addr add ${STATIC_IP}/${STATIC_CIDR} dev eth0 && log "ip addr add ${STATIC_IP}/${STATIC_CIDR}"
ip link set eth0 up && log "eth0 up"
ip route add default via ${STATIC_GATEWAY} 2>/dev/null && log "default route via ${STATIC_GATEWAY}" || log "WARN: route add failed"
echo "nameserver ${STATIC_DNS}" > /etc/resolv.conf && log "dns: ${STATIC_DNS}"
NETEOF

cat >> init.sh <<BOTTOMEOF

log "===== diag block ====="
log "kernel: \$(uname -a)"
log "/opt/app:"
ls -1 /opt/app | while read f; do log "  \$f"; done
log "===== diag end ====="

log "launching dgsvshs-bevy ${launch_args}"
log "Output -> /tmp/rust.log (tail -f /tmp/rust.log from shell)"

cd /opt/app
./dgsvshs-bevy ${launch_args} > /tmp/rust.log 2>&1 &
BEVY_PID=\$!
log "Bevy server launched as PID \$BEVY_PID"
BOTTOMEOF

cat >> init.sh <<'INITEOF_BOTTOM'

setsid /bin/sh -i </dev/console >/dev/console 2>&1 &
log "diagnostic shell PID $! — press Enter for prompt"

(
    wait $BEVY_PID
    BEXIT=$?
    echo "[init] Bevy server exited code $BEXIT — see /tmp/rust.log" > /dev/kmsg
) &

while true; do
    sleep 3600
done
INITEOF_BOTTOM

echo "==> Building aarch64 rootfs via Docker buildx..."
docker buildx build --platform linux/arm64 --load \
    -t "dgsvshs-bevy-microvm${flavor_suffix}:latest" \
    -f Dockerfile.microvm .

echo "==> Exporting rootfs..."
CID=$(docker create --platform linux/arm64 "dgsvshs-bevy-microvm${flavor_suffix}:latest")
rm -rf rootfs_dir
mkdir rootfs_dir
docker export "$CID" | tar -x -C rootfs_dir
docker rm "$CID" >/dev/null

mkdir -p rootfs_dir/lib/modules
cp modules/*.ko rootfs_dir/lib/modules/

echo "==> Packaging initramfs..."
cd rootfs_dir
find . | cpio -H newc -o 2>/dev/null | gzip -9 > "../initramfs${flavor_suffix}.cpio.gz"
cd ..

echo "==> Booting MicroVM via QEMU (UDP 4433 forwarded to host)..."
qemu-system-aarch64 \
  -machine virt,accel=hvf \
  -cpu host \
  -m 1024 \
  -kernel vmlinuz-virt \
  -initrd "initramfs${flavor_suffix}.cpio.gz" \
  -append "console=ttyAMA0 cgroup_disable=memory,pids" \
  -nographic \
  -netdev user,id=net0,hostfwd=udp::4433-:4433 \
  -device virtio-net-pci,netdev=net0
