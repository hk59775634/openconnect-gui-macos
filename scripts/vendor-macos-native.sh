#!/usr/bin/env bash
# Vendor libopenconnect + deps into SslVpnClient.Mac/Native/lib/<rid>/
# and build oc_progress_bridge.dylib. Requires Homebrew openconnect on the build machine.
# Compatible with macOS system bash 3.2 (no associative arrays).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NATIVE="$ROOT/SslVpnClient.Mac/Native"
HOST_ARCH="$(uname -m)"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  if [[ "$HOST_ARCH" == "arm64" ]]; then RID=osx-arm64; else RID=osx-x64; fi
fi

# Cross-arch: Apple Silicon packaging Intel → sonoma bottles
if [[ "$RID" == "osx-x64" && "$HOST_ARCH" == "arm64" ]]; then
  exec "$ROOT/scripts/vendor-macos-x64-bottles.sh"
fi
if [[ "$RID" == "osx-arm64" && "$HOST_ARCH" == "x86_64" ]]; then
  echo "ERROR: packaging osx-arm64 on Intel host is not supported here" >&2
  exit 1
fi

OUT="$NATIVE/lib/$RID"
mkdir -p "$OUT"
rm -f "$OUT"/*.dylib 2>/dev/null || true

BREW_PREFIX="$(brew --prefix 2>/dev/null || true)"
OC_PREFIX="$(brew --prefix openconnect 2>/dev/null || true)"
[[ -n "$OC_PREFIX" && -d "$OC_PREFIX" ]] || {
  echo "ERROR: brew openconnect required to vendor dylibs (build machine only)" >&2
  exit 1
}

echo "==> Vendor openconnect dylibs → $OUT (from $OC_PREFIX)"

realpath_py() {
  python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1"
}

copy_lib() {
  local src="$1"
  local base dest
  [[ -f "$src" ]] || return 1
  src="$(realpath_py "$src")"
  base="$(basename "$src")"
  dest="$OUT/$base"
  if [[ -f "$dest" ]]; then
    return 0
  fi
  cp -f "$src" "$dest"
  chmod 755 "$dest"
  echo "  + $base"
}

# Primary library
if [[ -f "$OC_PREFIX/lib/libopenconnect.5.dylib" ]]; then
  copy_lib "$OC_PREFIX/lib/libopenconnect.5.dylib"
else
  copy_lib "$OC_PREFIX/lib/libopenconnect.dylib"
fi

# BFS dependency closure (list file as queue)
QUEUE_FILE="$(mktemp)"
SEEN_FILE="$(mktemp)"
trap 'rm -f "$QUEUE_FILE" "$SEEN_FILE"' EXIT

for f in "$OUT"/libopenconnect*.dylib; do
  [[ -f "$f" ]] && echo "$f" >> "$QUEUE_FILE"
done

while [[ -s "$QUEUE_FILE" ]]; do
  cur="$(head -n1 "$QUEUE_FILE")"
  tail -n +2 "$QUEUE_FILE" > "${QUEUE_FILE}.rest" && mv "${QUEUE_FILE}.rest" "$QUEUE_FILE"
  [[ -f "$cur" ]] || continue

  otool -L "$cur" | awk '/^\t\//{print $1}' | while read -r dep; do
    case "$dep" in
      /usr/lib/*|/System/*) continue ;;
    esac
    [[ -f "$dep" ]] || continue
    base="$(basename "$(realpath_py "$dep")")"
    if grep -qxF "$base" "$SEEN_FILE" 2>/dev/null; then
      continue
    fi
    echo "$base" >> "$SEEN_FILE"
    if copy_lib "$dep"; then
      echo "$OUT/$base" >> "$QUEUE_FILE"
    fi
  done
done

# Short alias
for f in "$OUT"/libopenconnect*.dylib; do
  [[ -f "$f" ]] || continue
  ln -sfn "$(basename "$f")" "$OUT/libopenconnect.dylib"
  break
done

echo "==> Rewrite install names to @loader_path"
for dylib in "$OUT"/*.dylib; do
  [[ -f "$dylib" ]] || continue
  [[ -L "$dylib" ]] && continue
  base="$(basename "$dylib")"
  install_name_tool -id "@loader_path/$base" "$dylib" 2>/dev/null || true
  otool -L "$dylib" | awk '/^\t\//{print $1}' | while read -r dep; do
    case "$dep" in
      @loader_path/*|@rpath/*) continue ;;
      /usr/lib/*|/System/*) continue ;;
    esac
    depbase="$(basename "$(realpath_py "$dep" 2>/dev/null || echo "$dep")")"
    if [[ -f "$OUT/$depbase" ]]; then
      install_name_tool -change "$dep" "@loader_path/$depbase" "$dylib" 2>/dev/null || true
    fi
  done
  codesign -s - -f "$dylib" 2>/dev/null || true
done

echo "==> Bundle stock vpnc-script"
VPNC_SRC=""
for p in \
  "$OC_PREFIX/etc/vpnc/vpnc-script" \
  "$BREW_PREFIX/etc/vpnc/vpnc-script" \
  /opt/homebrew/etc/vpnc/vpnc-script \
  /usr/local/etc/vpnc/vpnc-script; do
  if [[ -f "$p" ]]; then VPNC_SRC="$p"; break; fi
done
[[ -n "$VPNC_SRC" ]] || { echo "ERROR: vpnc-script not found" >&2; exit 1; }
cp -f "$VPNC_SRC" "$NATIVE/vpnc-script"
chmod 755 "$NATIVE/vpnc-script"
cp -f "$NATIVE/vpnc-script" "$OUT/vpnc-script"

echo "==> Build oc_progress_bridge.dylib"
clang -dynamiclib -O2 -fPIC \
  -install_name "@loader_path/oc_progress_bridge.dylib" \
  -o "$OUT/oc_progress_bridge.dylib" \
  "$NATIVE/oc_progress_bridge.c"

# Flatten into Native/ for local dotnet run (host arch)
if [[ "$RID" == "osx-arm64" && "$HOST_ARCH" == "arm64" ]] || \
   [[ "$RID" == "osx-x64" && "$HOST_ARCH" == "x86_64" ]]; then
  find "$OUT" -maxdepth 1 \( -type f -o -type l \) \( -name '*.dylib' -o -name 'vpnc-script' \) \
    -exec cp -fL {} "$NATIVE/" \;
fi

echo "==> Done: $(find "$OUT" -maxdepth 1 -type f | wc -l | tr -d ' ') files in $OUT"
ls -lh "$OUT" | head -40
