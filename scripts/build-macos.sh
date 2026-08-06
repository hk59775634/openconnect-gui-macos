#!/usr/bin/env bash
# Build macOS Avalonia client (SslVpnClient.Mac).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH:/opt/homebrew/bin:/usr/local/bin"

CONFIG="${1:-Release}"
PROJECT="$ROOT/SslVpnClient.Mac/SslVpnClient.Mac.csproj"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Install .NET 8 SDK:"
  echo "  curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0"
  exit 1
fi

if [[ ! -f "$PROJECT" ]]; then
  echo "Project missing: $PROJECT"
  exit 1
fi

echo "==> Building SslVpnClient.Mac ($CONFIG)"
dotnet build "$PROJECT" -c "$CONFIG"

OUT="$ROOT/SslVpnClient.Mac/bin/$CONFIG/net8.0"
echo "==> Output: $OUT"
echo "Run:  dotnet run --project \"$PROJECT\" -c $CONFIG"
echo "Note: VPN connect needs openconnect + admin (Homebrew: brew install openconnect)"
