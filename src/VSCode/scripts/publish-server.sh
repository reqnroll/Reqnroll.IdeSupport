#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# publish-server.sh — Publish the Reqnroll LSP server for a given RID.
#
# Usage:
#   ./scripts/publish-server.sh [rid] [configuration]
#
#   rid           Runtime identifier (default: win-x64)
#   configuration Build configuration (default: Release)
#
# Examples:
#   ./scripts/publish-server.sh win-x64          # Windows x64 self-contained
#   ./scripts/publish-server.sh win-arm64        # Windows on ARM self-contained
#   ./scripts/publish-server.sh linux-x64         # Linux x64 self-contained
#   ./scripts/publish-server.sh linux-arm64       # Linux ARM64 self-contained
#   ./scripts/publish-server.sh osx-x64           # macOS Intel
#   ./scripts/publish-server.sh osx-arm64         # macOS Apple Silicon
#
# The published output is written to:
#   src/VSCode/server/<rid>/Reqnroll.IdeSupport.LSP.Server[.exe]
#
# This script is intentionally decoupled from the VS Code extension build —
# the server and extension are independently published.  The extension's
# build-vsix.sh script calls this as one step.
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
SERVER_PROJECT="$REPO_ROOT/src/LSP/Reqnroll.IdeSupport.LSP.Server/Reqnroll.IdeSupport.LSP.Server.csproj"

CONNECTOR_PROJECT="$REPO_ROOT/src/LSP/Reqnroll.IdeSupport.LSP.Connector/Connector/Connector.csproj"

RID="${1:-win-x64}"
CONFIGURATION="${2:-Release}"
OUTPUT_DIR="$REPO_ROOT/src/VSCode/server"

echo "==> Publishing server for RID=$RID (Configuration=$CONFIGURATION)"
echo "    Project: $SERVER_PROJECT"
echo "    Output:  $OUTPUT_DIR/$RID/"

# Restore the Connector project with RID first (required for multi-TFM resolution)
dotnet restore "$CONNECTOR_PROJECT" --runtime "$RID" --nologo 2>&1 | grep -v "^$" | grep -v "^  Restored" || true

dotnet publish "$SERVER_PROJECT" \
  --configuration "$CONFIGURATION" \
  --runtime "$RID" \
  --self-contained true \
  --nologo \
  --output "$OUTPUT_DIR/$RID"

echo ""
echo "==> Server published successfully."
echo "    Binary: $OUTPUT_DIR/$RID/Reqnroll.IdeSupport.LSP.Server"
ls -lh "$OUTPUT_DIR/$RID/Reqnroll.IdeSupport.LSP.Server"* 2>/dev/null || true
