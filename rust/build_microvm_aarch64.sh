#!/usr/bin/env bash

set -e

echo "==> Compiling Bevy Server..."
cargo zigbuild --target aarch64-unknown-linux-musl --release

mkdir -p .microvm_aarch64
cd .microvm_aarch64

KERNEL_VERSION="6.18.33-r0"
KERNEL_MOD_DIR="6.18.33-0-virt"

# Pull both kernel and modules from the same apk so versions always match.
# Alpine's netboot mirror lags the apk repo, which causes vermagic mismatch
# if you mix sources.
if [ ! -f "vmlinuz-virt" ] || [ ! -d "modules" ]; then
    echo "==> Fetching kernel + virtio_net modules from Alpine apk..."
    curl -sLO "https://dl-cdn.alpinelinux.org/alpine/latest-stable/main/aarch64/linux-virt-${KERNEL_VERSION}.apk"
    mkdir -p apk_extract modules
    tar -xzf "linux-virt-${KERNEL_VERSION}.apk" -C apk_extract 2>/dev/null
    cp apk_extract/boot/vmlinuz-virt vmlinuz-virt
    # vmlinuz-virt ships virtio_net as a module (CONFIG_VIRTIO_NET=m). Load
    # order at init: failover → net_failover → virtio_net.
    for m in failover net_failover virtio_net; do
        src=$(find "apk_extract/lib/modules/${KERNEL_MOD_DIR}" -name "${m}.ko.gz")
        gunzip -c "$src" > "modules/${m}.ko"
    done
    rm -rf apk_extract "linux-virt-${KERNEL_VERSION}.apk"
else
    echo "==> Kernel + modules already extracted. Skipping."
fi

echo "==> Packaging initramfs..."
mkdir -p rootfs_dir
cp ../target/aarch64-unknown-linux-musl/release/cli rootfs_dir/init
mkdir -p rootfs_dir/modules
cp modules/*.ko rootfs_dir/modules/

cd rootfs_dir
# Suppress the cpio blocks output to keep the terminal clean
find . | cpio -H newc -o 2>/dev/null | gzip -9 > ../initramfs.cpio.gz
cd ..

echo "==> Booting MicroVM via QEMU..."
qemu-system-aarch64 \
  -machine virt,accel=hvf \
  -cpu host \
  -m 1024 \
  -kernel vmlinuz-virt \
  -initrd initramfs.cpio.gz \
  -append "console=ttyAMA0 panic=1 cgroup_disable=memory,pids" \
  -nographic \
  -netdev user,id=net0,hostfwd=udp::4433-:4433 \
  -device virtio-net-pci,netdev=net0
