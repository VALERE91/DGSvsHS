#!/usr/bin/env bash
# build_and_deploy.sh — Linux/macOS build + Unity Plugins deploy.
#
# Builds the dgsvshs_socket cdylib in release and copies the resulting shared library into the
# Unity project's plugin folder. On macOS the output is .dylib, on Linux .so; both are detected
# and renamed to the form Unity's DllImport expects.

set -euo pipefail

crate_root="$(cd "$(dirname "$0")/.." && pwd)"
repo_root="$(cd "$crate_root/../.." && pwd)"
plugin_dir="$repo_root/DGSvsHS/Assets/Plugins/x86_64"

echo "[build] cargo build --release --lib in $crate_root"
(cd "$crate_root" && cargo build --release --lib)

case "$(uname -s)" in
    Linux*)
        src="$crate_root/target/release/libdgsvshs_socket.so"
        dst="$plugin_dir/libdgsvshs_socket.so"
        ;;
    Darwin*)
        src="$crate_root/target/release/libdgsvshs_socket.dylib"
        dst="$plugin_dir/libdgsvshs_socket.dylib"
        ;;
    *)
        echo "unsupported platform: $(uname -s)" >&2
        exit 1
        ;;
esac

if [ ! -f "$src" ]; then
    echo "expected output not found: $src" >&2
    exit 1
fi

mkdir -p "$plugin_dir"
cp -f "$src" "$dst"

echo "[deploy] copied to $dst"
echo "[done] Unity will reimport on next focus."
