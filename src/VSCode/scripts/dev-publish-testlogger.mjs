#!/usr/bin/env node
// ---------------------------------------------------------------------------
// dev-publish-testlogger.mjs — Republish the TestLogger for F5 dev-mode
// launches (LSP-server outcome pipeline, #700/#702), but only when source
// has changed since the last dev publish. Mirrors dev-publish-server.mjs.
//
// The Extension Development Host (F5) resolves the logger from:
//   src/Core/Reqnroll.IdeSupport.TestLogger/bin/Release/netstandard2.0/
// (see resolveTestLoggerPath's non-production branch in src/extension.ts).
// ---------------------------------------------------------------------------
import { execFileSync } from 'node:child_process';
import { existsSync, statSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const SCRIPT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(SCRIPT_DIR, '..', '..', '..');
const LOGGER_DIR = path.join(REPO_ROOT, 'src', 'Core', 'Reqnroll.IdeSupport.TestLogger');
const LOGGER_PROJECT = path.join(LOGGER_DIR, 'Reqnroll.IdeSupport.TestLogger.csproj');
const PUBLISH_DIR = path.join(LOGGER_DIR, 'bin', 'Release', 'netstandard2.0');
const PUBLISHED_DLL = path.join(PUBLISH_DIR, 'Reqnroll.IdeSupport.TestLogger.dll');

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
  return newestSourceMtime(LOGGER_DIR) > publishedMtime;
}

if (!isStale()) {
  console.log('==> TestLogger publish is up to date, skipping republish.');
  process.exit(0);
}

console.log('==> TestLogger source changed since last dev publish, republishing...');

execFileSync(
  'dotnet',
  ['publish', LOGGER_PROJECT, '--configuration', 'Release', '--nologo'],
  { stdio: 'inherit' },
);

console.log('==> Dev TestLogger republished.');
