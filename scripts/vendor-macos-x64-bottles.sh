#!/usr/bin/env bash
# On Apple Silicon: download Homebrew *sonoma* (x86_64) bottles and vendor
# dylibs into SslVpnClient.Mac/Native/lib/osx-x64 for Intel Mac packages.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/SslVpnClient.Mac/Native/lib/osx-x64"
NATIVE="$ROOT/SslVpnClient.Mac/Native"
STAGE="$(mktemp -d /tmp/ocg-x64-bottles.XXXXXX)"
trap 'rm -rf "$STAGE"' EXIT

mkdir -p "$OUT"
rm -f "$OUT"/*.dylib 2>/dev/null || true

# formula -> needed for openconnect runtime
FORMULAS=(openconnect gnutls gmp nettle p11-kit gettext stoken libtasn1 libidn2 libunistring libevent unbound libnghttp2)

download_bottle() {
  local formula="$1"
  local json url sha cellar name tar
  json="$(curl -fsSL "https://formulae.brew.sh/api/formula/${formula}.json")"
  url="$(python3 -c 'import json,sys; j=json.load(sys.stdin); f=j["bottle"]["stable"]["files"];
print((f.get("sonoma") or f.get("ventura") or {}).get("url",""))' <<<"$json")"
  sha="$(python3 -c 'import json,sys; j=json.load(sys.stdin); f=j["bottle"]["stable"]["files"];
print((f.get("sonoma") or f.get("ventura") or {}).get("sha256",""))' <<<"$json")"
  [[ -n "$url" ]] || { echo "no sonoma bottle for $formula" >&2; return 1; }
  tar="$STAGE/${formula}.tar.gz"
  echo "==> $formula"
  curl -fsSL -H "Authorization: Bearer QQ==" -o "$tar" "$url"
  echo "$sha  $tar" | shasum -a 256 -c -
  mkdir -p "$STAGE/$formula"
  tar -xzf "$tar" -C "$STAGE/$formula"
}

echo "==> Fetch x86_64 (sonoma) bottles → $OUT"
for f in "${FORMULAS[@]}"; do
  download_bottle "$f"
done

# Collect all .dylib from extracted bottles
find "$STAGE" -type f -name '*.dylib' ! -name '*-*.dylib' 2>/dev/null | while read -r _; do :; done
find "$STAGE" -type f \( -name '*.dylib' -o -name 'lib*.*.dylib' \) | while read -r src; do
  base="$(basename "$src")"
  # skip debug / weird
  case "$base" in
    *.dSYM*) continue ;;
  esac
  cp -f "$src" "$OUT/$base"
done

# Prefer versioned libopenconnect.5.dylib + short alias
if [[ -f "$OUT/libopenconnect.5.dylib" ]]; then
  ln -sfn libopenconnect.5.dylib "$OUT/libopenconnect.dylib"
elif [[ -f "$OUT/libopenconnect.dylib" ]]; then
  :
else
  echo "ERROR: libopenconnect missing after bottle extract" >&2
  ls -la "$OUT" | head
  exit 1
fi

# Verify arch
ARCH="$(file -b "$OUT/libopenconnect.dylib" | tr '\n' ' ')"
echo "libopenconnect: $ARCH"
echo "$ARCH" | grep -qi 'x86_64' || {
  echo "ERROR: expected x86_64 dylib, got: $ARCH" >&2
  exit 1
}

echo "==> Rewrite install names to @loader_path"
realpath_py() { python3 -c 'import os,sys; print(os.path.realpath(sys.argv[1]))' "$1"; }
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
    # also try without realpath basename of install name
    depbase2="$(basename "$dep")"
    if [[ -f "$OUT/$depbase" ]]; then
      install_name_tool -change "$dep" "@loader_path/$depbase" "$dylib" 2>/dev/null || true
    elif [[ -f "$OUT/$depbase2" ]]; then
      install_name_tool -change "$dep" "@loader_path/$depbase2" "$dylib" 2>/dev/null || true
    fi
  done
  codesign -s - -f "$dylib" 2>/dev/null || true
done

# stock vpnc-script from arm brew is arch-independent shell
VPNC_SRC=""
for p in \
  "$(brew --prefix openconnect 2>/dev/null)/etc/vpnc/vpnc-script" \
  /opt/homebrew/etc/vpnc/vpnc-script \
  /usr/local/etc/vpnc/vpnc-script; do
  [[ -f "$p" ]] && { VPNC_SRC="$p"; break; }
done
[[ -n "$VPNC_SRC" ]] || { echo "ERROR: vpnc-script not found" >&2; exit 1; }
cp -f "$VPNC_SRC" "$NATIVE/vpnc-script"
chmod 755 "$NATIVE/vpnc-script"
cp -f "$NATIVE/vpnc-script" "$OUT/vpnc-script"

echo "==> Build oc_progress_bridge.dylib (x86_64)"
clang -arch x86_64 -dynamiclib -O2 -fPIC \
  -install_name "@loader_path/oc_progress_bridge.dylib" \
  -o "$OUT/oc_progress_bridge.dylib" \
  "$NATIVE/oc_progress_bridge.c"
codesign -s - -f "$OUT/oc_progress_bridge.dylib" 2>/dev/null || true

echo "==> Done: $(find "$OUT" -maxdepth 1 -type f -name '*.dylib' | wc -l | tr -d ' ') dylibs in $OUT"
ls -lh "$OUT" | head -40
