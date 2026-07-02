#!/usr/bin/env bash

set -e

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$PROJECT_ROOT"

# Prerequisites on the build host:
#   - rustup target add x86_64-unknown-linux-musl  +  cargo install cargo-zigbuild  +  zig
#   - docker with buildx (no binfmt needed — same-arch x86_64 builds run native)
#   - qemu-system-x86_64 + cpio + gzip + curl
# On Linux x86_64 host (Ryzen / Proxmox / Hetzner): accel=kvm is native speed.
# On macOS Intel: accel=hvf. On WSL or any non-KVM host: drop the accel flag (TCG).
#
# Usage:
#   ./build_microvm_x86_64.sh             # normal build (-> bevy-microvm.iso)
#   ./build_microvm_x86_64.sh --god-mode  # build with --god-mode CLI flag baked in
#                                         # (init.sh launches `./dgsvshs-bevy --god-mode`)
#                                         # (-> bevy-microvm-godmode.iso)

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
#   STATIC_IP=192.168.1.50 STATIC_GATEWAY=192.168.1.1 ./build_microvm_x86_64.sh
STATIC_IP="${STATIC_IP:-192.168.0.205}"
STATIC_CIDR="${STATIC_CIDR:-24}"
STATIC_GATEWAY="${STATIC_GATEWAY:-192.168.0.1}"
STATIC_DNS="${STATIC_DNS:-8.8.8.8}"
echo "==> Static IP for this build: ${STATIC_IP}/${STATIC_CIDR} gw=${STATIC_GATEWAY} dns=${STATIC_DNS}"

echo "==> Compiling Bevy Server..."
cargo zigbuild --target x86_64-unknown-linux-musl --release

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

# Stage the freshly-built static-musl binary for the Dockerfile COPY.
rm -rf publish
mkdir -p publish
cp "../target/x86_64-unknown-linux-musl/release/cli" publish/dgsvshs-bevy
chmod +x publish/dgsvshs-bevy

cat > Dockerfile.microvm <<'DOCKEREOF'
FROM --platform=linux/amd64 alpine:3.21

# Rust binary is fully static-musl; ca-certificates for any TLS outbound,
# procps/less for the diagnostic shell, iproute2 for full-featured `ip`
# (busybox `ip addr add` races eth0 enumeration and silently no-ops).
RUN apk add --no-cache ca-certificates procps less iproute2

COPY publish/dgsvshs-bevy /opt/app/dgsvshs-bevy
RUN chmod +x /opt/app/dgsvshs-bevy

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

for m in failover net_failover virtio_net; do
    insmod /lib/modules/\${m}.ko 2>/dev/null
    log "insmod \${m}"
done

ip link set lo up   && log "lo up"

# Kill any DHCP client that might have auto-started (kernel IP-PnP, ifupdown
# hook, or apk post-install). Without this the static IP we add below can get
# wiped seconds later when the DHCP lease comes in.
for d in udhcpc dhcpcd dhclient; do
    pkill -9 \$d 2>/dev/null && log "killed lingering \$d"
done

# Wait briefly for eth0 to be enumerated by virtio_net.
for i in 1 2 3 4 5 6 7 8 9 10; do
    ip link show eth0 >/dev/null 2>&1 && break
    sleep 0.2
done

ip link set eth0 up && log "eth0 up"

# Flush before adding — clears anything the kernel auto-configured or that a
# late DHCP response may have laid down.
ip addr flush dev eth0
ip addr add ${STATIC_IP}/${STATIC_CIDR} dev eth0 && log "ip addr add ${STATIC_IP}/${STATIC_CIDR}"
ip route add default via ${STATIC_GATEWAY} 2>/dev/null && log "default route via ${STATIC_GATEWAY}" || log "WARN: failed to add default route via ${STATIC_GATEWAY}"
echo "nameserver ${STATIC_DNS}" > /etc/resolv.conf && log "dns: ${STATIC_DNS}"
log "ip addr now: \$(ip -4 -o addr show eth0 | awk '{print \$4}')"

# Final sanity dump so we can see post-init state in the boot console.
log "=== final ip a ==="
ip -4 -o addr show 2>&1 | while read line; do log "  \$line"; done
log "=== running procs ==="
ps -A -o pid,comm 2>&1 | head -30 | while read line; do log "  \$line"; done
log "=================="

echo ""
echo "######################################################"
echo "##  DGSvsHS Bevy MicroVM diag                       ##"
echo "######################################################"
uname -a
echo "--- /opt/app contents ---"
ls -la /opt/app
echo ""
echo "######################################################"
echo "##  Launching dgsvshs-bevy                          ##"
echo "######################################################"
echo ""

log "launching dgsvshs-bevy ${launch_args}"
log "Output -> /tmp/rust.log (tail -f /tmp/rust.log from shell to watch)"

# Guardian: something on this system periodically wipes the static IP and
# replaces it with QEMU SLIRP's 10.0.2.15. Source unknown (kernel autoconf?
# Proxmox SDN? cloud-init?) but we just re-assert every 3 seconds and log it.
(
    while true; do
        sleep 3
        if ! ip -4 addr show eth0 2>/dev/null | grep -q '${STATIC_IP}/'; then
            ip addr flush dev eth0 2>/dev/null
            ip addr add ${STATIC_IP}/${STATIC_CIDR} dev eth0 2>/dev/null
            ip route add default via ${STATIC_GATEWAY} 2>/dev/null
            echo "[guardian] re-asserted ${STATIC_IP} on eth0 at \$(date +%H:%M:%S)" > /dev/kmsg 2>/dev/null
        fi
    done
) &
log "ip guardian PID \$! — re-asserts ${STATIC_IP} if anything wipes it"

cd /opt/app
# Server output to log file so it doesn't interleave with the shell on /dev/console.
./dgsvshs-bevy ${launch_args} > /tmp/rust.log 2>&1 &
BEVY_PID=\$!
log "Bevy server launched as PID \$BEVY_PID"

setsid /bin/sh -i </dev/console >/dev/console 2>&1 &
SHELL_PID=\$!
log "diagnostic shell PID \$SHELL_PID — press Enter for prompt"

# Poll server in the background; surface its exit code via kmsg.
(
    wait \$BEVY_PID
    BEXIT=\$?
    echo "[init] Bevy server exited code \$BEXIT — see /tmp/rust.log for output" > /dev/kmsg
) &

# PID 1 must never exit — sleep forever.
while true; do
    sleep 3600
done
INITEOF

echo "==> Building x86_64 rootfs via Docker buildx..."
docker buildx build --platform linux/amd64 --load \
    -t "dgsvshs-bevy-microvm-x64${flavor_suffix}:latest" \
    -f Dockerfile.microvm .

echo "==> Exporting rootfs..."
CID=$(docker create --platform linux/amd64 "dgsvshs-bevy-microvm-x64${flavor_suffix}:latest")
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
cp vmlinuz-virt                          iso_staging/boot/vmlinuz
cp "initramfs${flavor_suffix}.cpio.gz"   iso_staging/boot/initrd.img

flavor_label="${flavor_suffix:+ (godmode)}"
cat > iso_staging/boot/grub/grub.cfg <<GRUBEOF
set timeout=2
set default=0
menuentry "DGSvsHS Bevy MicroVM${flavor_label}" {
    linux  /boot/vmlinuz console=tty0 console=ttyS0 cgroup_disable=memory,pids ip=off
    initrd /boot/initrd.img
}
GRUBEOF

grub-mkrescue -o "bevy-microvm${flavor_suffix}.iso" iso_staging >/dev/null 2>&1
rm -rf iso_staging

echo "==> ISO ready: $(pwd)/bevy-microvm${flavor_suffix}.iso"
echo "    Upload via Proxmox UI > Storage > ISO Images > Upload."
echo "    Create VM, set CD/DVD to this ISO, start. Logs in the Console tab."
