#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# publish-testlogger.sh — Publish the bundled Reqnroll.IdeSupport.TestLogger
# (LSP-server outcome pipeline, #700/#702).
#
# Usage:
#   ./scripts/publish-testlogger.sh [configuration]
#
#   configuration Build configuration (default: Release)
#
# Unlike publish-server.sh, there is no RID to select: the logger targets
# netstandard2.0 with no self-contained runtime (see its own project file's
# remarks) and loads inside whichever dotnet test/vstest process is already
# running, on any OS — one build serves every platform.
#
# The published output is written to:
#   src/VSCode/testlogger/Reqnroll.IdeSupport.TestLogger.dll
#
# This script is intentionally decoupled from the VS Code extension build —
# build-vsix.sh calls this as one step, same as publish-server.sh.
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
LOGGER_PROJECT="$REPO_ROOT/src/Core/Reqnroll.IdeSupport.TestLogger/Reqnroll.IdeSupport.TestLogger.csproj"

CONFIGURATION="${1:-Release}"
OUTPUT_DIR="$REPO_ROOT/src/VSCode/testlogger"

echo "==> Publishing TestLogger (Configuration=$CONFIGURATION)"
echo "    Project: $LOGGER_PROJECT"
echo "    Output:  $OUTPUT_DIR/"

dotnet publish "$LOGGER_PROJECT" \
  --configuration "$CONFIGURATION" \
  --nologo \
  --output "$OUTPUT_DIR"

echo ""
echo "==> TestLogger published successfully."
echo "    Assembly: $OUTPUT_DIR/Reqnroll.IdeSupport.TestLogger.dll"
ls -lh "$OUTPUT_DIR/Reqnroll.IdeSupport.TestLogger.dll" 2>/dev/null || true
