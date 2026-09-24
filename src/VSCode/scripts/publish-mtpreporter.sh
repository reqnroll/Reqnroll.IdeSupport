#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# publish-mtpreporter.sh — Publish the bundled Reqnroll.IdeSupport.TestReporter.MTP
# (LSP-server outcome pipeline, issue #715 phase 4).
#
# Usage:
#   ./scripts/publish-mtpreporter.sh [configuration]
#
#   configuration Build configuration (default: Release)
#
# Like publish-testlogger.sh, there is no RID to select. What the extension uses from
# the published output is the reporter's source bundle (issue #741) —
# Reqnroll.IdeSupport.TestReporter.MTP.targets + ReporterSource/*.cs, Content items of
# the reporter project — which a project-local obj/<Project>.csproj.reqnroll-ide.targets
# stub imports to compile the reporter into the user's own test assembly (see
# src/testOutcomes/mtpProjectStubs.ts).
#
# The published output is written to:
#   src/VSCode/mtpreporter/Reqnroll.IdeSupport.TestReporter.MTP.targets
#   src/VSCode/mtpreporter/ReporterSource/*.cs
#
# This script is intentionally decoupled from the VS Code extension build —
# build-vsix.sh calls this as one step, same as publish-testlogger.sh.
# ---------------------------------------------------------------------------
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
REPORTER_PROJECT="$REPO_ROOT/src/Core/Reqnroll.IdeSupport.TestReporter.MTP/Reqnroll.IdeSupport.TestReporter.MTP.csproj"

CONFIGURATION="${1:-Release}"
OUTPUT_DIR="$REPO_ROOT/src/VSCode/mtpreporter"

echo "==> Publishing TestReporter.MTP (Configuration=$CONFIGURATION)"
echo "    Project: $REPORTER_PROJECT"
echo "    Output:  $OUTPUT_DIR/"

dotnet publish "$REPORTER_PROJECT" \
  --configuration "$CONFIGURATION" \
  --nologo \
  --output "$OUTPUT_DIR"

echo ""
echo "==> TestReporter.MTP published successfully."
echo "    Bundle:   $OUTPUT_DIR/Reqnroll.IdeSupport.TestReporter.MTP.targets"
ls -lh "$OUTPUT_DIR/Reqnroll.IdeSupport.TestReporter.MTP.targets" "$OUTPUT_DIR/ReporterSource" 2>/dev/null || true
