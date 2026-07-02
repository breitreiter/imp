#!/usr/bin/env bash
# Build imp and install it to ~/.local so `imp` is on PATH from any repo.
#
# Layout produced:
#   ~/.local/share/imp/        — full build output (apphost + imp.dll + deps + content)
#   ~/.local/bin/imp           — symlink to the apphost above
#
# The build output is framework-dependent: the .NET 10 runtime must be
# installed on the machine. Re-run after any code change to refresh the
# on-PATH binary (a plain `dotnet build` only updates the repo's bin/,
# not the installed copy).
#
# Usage:
#   ./install.sh            # Release build (default)
#   ./install.sh Debug      # Debug build (keeps .pdb for stack traces)
set -euo pipefail

CONFIG="${1:-Release}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/imp"
BIN_DIR="$HOME/.local/bin"
OUT_DIR="bin/$CONFIG/net10.0"

echo "==> building imp ($CONFIG)"
dotnet build -c "$CONFIG" --nologo

if [ ! -f "$OUT_DIR/imp" ]; then
  echo "error: expected apphost at $OUT_DIR/imp not found" >&2
  exit 1
fi

# Preserve an existing installed appsettings.json — it holds the user's
# provider config/keys and is the active config for global invocations
# (cwd has none, so imp falls back to the one next to the DLL).
TMP_CONFIG=""
if [ -f "$DATA_DIR/appsettings.json" ]; then
  TMP_CONFIG="$(mktemp)"
  cp "$DATA_DIR/appsettings.json" "$TMP_CONFIG"
fi

echo "==> syncing $OUT_DIR -> $DATA_DIR"
rm -rf "$DATA_DIR"
mkdir -p "$DATA_DIR" "$BIN_DIR"
cp -r "$OUT_DIR/." "$DATA_DIR/"

if [ -n "$TMP_CONFIG" ]; then
  cp "$TMP_CONFIG" "$DATA_DIR/appsettings.json"
  rm -f "$TMP_CONFIG"
  echo "    (preserved existing appsettings.json)"
fi

ln -sfn "$DATA_DIR/imp" "$BIN_DIR/imp"
echo "==> linked $BIN_DIR/imp -> $DATA_DIR/imp"

case ":$PATH:" in
  *":$BIN_DIR:"*) ;;
  *) echo "warning: $BIN_DIR is not on PATH — add it to use \`imp\` directly" >&2 ;;
esac

echo "==> done — run \`imp help\` to verify"
