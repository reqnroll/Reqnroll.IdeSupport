// copy-changelog.mjs — copies the repo-root CHANGELOG.md next to package.json so `vsce package`
// ships it and the Marketplace shows it on the extension's "Changelog" tab. The copy is
// git-ignored; the root file stays the single source of truth for all three IDE clients.
//
// Runs from the `vscode:prepublish` npm script (so `vsce package` picks it up automatically).

import { copyFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(new URL('../../../CHANGELOG.md', import.meta.url));
const target = fileURLToPath(new URL('../CHANGELOG.md', import.meta.url));

copyFileSync(source, target);
console.log(`Copied ${source} -> ${target}`);
