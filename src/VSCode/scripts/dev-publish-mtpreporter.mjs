#!/usr/bin/env node
// ---------------------------------------------------------------------------
// dev-publish-mtpreporter.mjs — Rewrite the TestReporter.MTP source bundle for F5 dev-mode
// launches (LSP-server outcome pipeline, issues #715/#741), but only when the reporter's
// sources have changed since the last dev publish. Mirrors dev-publish-testlogger.mjs.
//
// The Extension Development Host (F5) resolves the bundle from:
//   src/Core/Reqnroll.IdeSupport.TestReporter.MTP/bin/Release/bundle/
// (see resolveMtpReporterDirectory's non-production branch in
// src/testOutcomes/mtpReporterPath.ts), written by the reporter project's
// PublishReporterBundle target — the same target publish-mtpreporter.sh uses for release.
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { existsSync, statSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(SCRIPT_DIR, '..', '..', '..');
const CORE_DIR = path.join(REPO_ROOT, 'src', 'Core');
const REPORTER_DIR = path.join(CORE_DIR, 'Reqnroll.IdeSupport.TestReporter.MTP');
// The bundle is generated from the reporter's own sources AND the shared Common sources.
const SOURCE_DIRS = [REPORTER_DIR, path.join(CORE_DIR, 'Reqnroll.IdeSupport.TestReporter.Common')];
const REPORTER_PROJECT = path.join(REPORTER_DIR, 'Reqnroll.IdeSupport.TestReporter.MTP.csproj');
const BUNDLE_DIR = path.join(REPORTER_DIR, 'bin', 'Release', 'bundle');
// PublishReporterBundle touches this on every publish; the copied bundle files keep their
// source timestamps, so they can't tell a fresh publish from a stale one.
const BUNDLE_STAMP = path.join(BUNDLE_DIR, '.bundle-stamp');

const IGNORED_DIR_NAMES = new Set(['bin', 'obj', 'node_modules', '.git']);

function newestSourceMtime(dir) {
  let newest = 0;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (IGNORED_DIR_NAMES.has(entry.name)) continue;
      newest = Math.max(newest, newestSourceMtime(path.join(dir, entry.name)));
    } else {
      newest = Math.max(newest, statSync(path.join(dir, entry.name)).mtimeMs);
    }
  }
  return newest;
}

function isStale() {
  if (!existsSync(BUNDLE_STAMP)) return true;
  const publishedMtime = statSync(BUNDLE_STAMP).mtimeMs;
  return SOURCE_DIRS.some((dir) => newestSourceMtime(dir) > publishedMtime);
}

if (!isStale()) {
  console.log('==> TestReporter.MTP source bundle is up to date, skipping republish.');
  process.exit(0);
}

console.log('==> TestReporter.MTP source changed since last dev publish, rewriting the bundle...');

execFileSync(
  'dotnet',
  [
    'msbuild',
    REPORTER_PROJECT,
    '-t:PublishReporterBundle',
    '-p:Configuration=Release',
    `-p:ReporterBundleDir=${BUNDLE_DIR}`,
    '-nologo',
  ],
  { stdio: 'inherit' },
);

console.log('==> Dev TestReporter.MTP source bundle rewritten.');
