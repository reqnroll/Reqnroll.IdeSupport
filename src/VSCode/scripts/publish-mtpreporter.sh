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
# Like publish-testlogger.sh, there is no RID to select: the reporter targets net8.0
# with no self-contained runtime and loads inside whichever MTP test host process is
# already running, on any OS — one build serves every platform. Unlike the logger it
# is never referenced via runsettings/--test-adapter-path; it's injected ephemerally
# via a HintPath <Reference> written into a throwaway .targets file at extension
# activation (see src/testOutcomes/mtpEphemeralInjection.ts).
#
# The published output is written to:
#   src/VSCode/mtpreporter/Reqnroll.IdeSupport.TestReporter.MTP.dll
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
echo "    Assembly: $OUTPUT_DIR/Reqnroll.IdeSupport.TestReporter.MTP.dll"
ls -lh "$OUTPUT_DIR/Reqnroll.IdeSupport.TestReporter.MTP.dll" 2>/dev/null || true
