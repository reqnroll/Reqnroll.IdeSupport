import * as path from 'path';

import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
  try {
    // The folder containing the Extension Manifest (package.json)
    const extensionDevelopmentPath = path.resolve(__dirname, '..', '..');

    // The path to the test bootstrapper (registers Mocha's suite/test globals, then loads
    // extension.test.js which pulls in the other suites via its own imports).
    const extensionTestsPath = path.resolve(__dirname, 'index');

    // The repo root — four levels above out/test (out/test -> VSCode -> src -> root). Opened
    // as the workspace folder so tests can see .csproj/.slnx files outside src/VSCode (see
    // projectManager.test.ts's discovery test): without an explicit folder here,
    // the Extension Development Host defaults to opening extensionDevelopmentPath itself
    // (src/VSCode), which is too narrow a root for that test's assertions.
    const repoRoot = path.resolve(__dirname, '..', '..', '..', '..');

    // Download VS Code, unzip it and run the integration test
    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      launchArgs: [
        repoRoot,
        // Use a temporary workspace to avoid opening the last-used workspace
        '--new-window',
        // Disable all other extensions for clean test isolation
        '--disable-extensions',
      ],
    });
  } catch (err) {
    console.error('Failed to run tests:', err);
    process.exit(1);
  }
}

void main();
