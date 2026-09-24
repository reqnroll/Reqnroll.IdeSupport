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
# Like publish-testlogger.sh, there is no RID to select. What ships is the reporter's
# source bundle only (issue #741) — Reqnroll.IdeSupport.TestReporter.MTP.targets +
# ReporterSource/*.cs, written by the reporter project's PublishReporterBundle target
# (not `dotnet publish`, which would also copy assemblies nothing uses). A project-local
# obj/<Project>.csproj.reqnroll-ide.targets stub imports that .targets file to compile
# the reporter into the user's own test assembly (see src/testOutcomes/mtpProjectStubs.ts).
#
# The bundle is written to (replacing the directory's previous contents):
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

echo "==> Writing the TestReporter.MTP source bundle (Configuration=$CONFIGURATION)"
echo "    Project: $REPORTER_PROJECT"
echo "    Output:  $OUTPUT_DIR/"

dotnet msbuild "$REPORTER_PROJECT" \
  -t:PublishReporterBundle \
  -p:Configuration="$CONFIGURATION" \
  -p:ReporterBundleDir="$OUTPUT_DIR" \
  -nologo

echo ""
echo "==> TestReporter.MTP source bundle written."
ls -lh "$OUTPUT_DIR/Reqnroll.IdeSupport.TestReporter.MTP.targets" "$OUTPUT_DIR/ReporterSource" 2>/dev/null || true
