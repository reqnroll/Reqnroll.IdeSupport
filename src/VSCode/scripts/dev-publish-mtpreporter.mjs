#!/usr/bin/env node
// ---------------------------------------------------------------------------
// dev-publish-mtpreporter.mjs — Republish the TestReporter.MTP for F5 dev-mode
// launches (LSP-server outcome pipeline, issue #715 phase 4), but only when
// source has changed since the last dev publish. Mirrors dev-publish-testlogger.mjs.
//
// The Extension Development Host (F5) resolves the reporter from:
//   src/Core/Reqnroll.IdeSupport.TestReporter.MTP/bin/Release/net8.0/
// (see resolveMtpReporterDirectory's non-production branch in
// src/testOutcomes/mtpReporterPath.ts).
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { existsSync, statSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(SCRIPT_DIR, '..', '..', '..');
const REPORTER_DIR = path.join(REPO_ROOT, 'src', 'Core', 'Reqnroll.IdeSupport.TestReporter.MTP');
const REPORTER_PROJECT = path.join(REPORTER_DIR, 'Reqnroll.IdeSupport.TestReporter.MTP.csproj');
const PUBLISH_DIR = path.join(REPORTER_DIR, 'bin', 'Release', 'net8.0');
const PUBLISHED_DLL = path.join(PUBLISH_DIR, 'Reqnroll.IdeSupport.TestReporter.MTP.dll');

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
  if (!existsSync(PUBLISHED_DLL)) return true;
  const publishedMtime = statSync(PUBLISHED_DLL).mtimeMs;
  return newestSourceMtime(REPORTER_DIR) > publishedMtime;
}

if (!isStale()) {
  console.log('==> TestReporter.MTP publish is up to date, skipping republish.');
  process.exit(0);
}

console.log('==> TestReporter.MTP source changed since last dev publish, republishing...');

execFileSync(
  'dotnet',
  ['publish', REPORTER_PROJECT, '--configuration', 'Release', '--nologo'],
  { stdio: 'inherit' },
);

console.log('==> Dev TestReporter.MTP republished.');
