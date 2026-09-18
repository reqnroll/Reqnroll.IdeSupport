#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# build-vsix.sh — Build the Reqnroll VS Code extension .vsix package.
#
# Usage:
#   ./scripts/build-vsix.sh [rid]
#
#   rid    Target runtime identifier (default: win-x64)
#
# Steps:
#   1. Publish the LSP server for the target RID (calls publish-server.sh)
#   2. Publish the bundled TestLogger (calls publish-testlogger.sh; no RID needed)
#   3. npm install (if node_modules missing)
#   4. npm run compile (TypeScript → JavaScript)
#   5. vsce package (produce .vsix)
#
# The server and extension builds are intentionally kept as separate steps
# so each can be run and verified independently.
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
VSC_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
RID="${1:-win-x64}"

echo "==> [1/5] Publishing server for $RID ..."
bash "$SCRIPT_DIR/publish-server.sh" "$RID"

echo ""
echo "==> [2/5] Publishing TestLogger ..."
bash "$SCRIPT_DIR/publish-testlogger.sh"

echo ""
echo "==> [3/5] Installing npm dependencies ..."
cd "$VSC_DIR"
if [ ! -d "node_modules" ]; then
  npm install
fi

echo ""
echo "==> [4/5] Compiling TypeScript ..."
npm run compile

echo ""
echo "==> [5/5] Packaging VSIX ..."
npx @vscode/vsce package

echo ""
echo "==> Done.  The .vsix is in $VSC_DIR/"
ls -lh "$VSC_DIR"/*.vsix 2>/dev/null || true
