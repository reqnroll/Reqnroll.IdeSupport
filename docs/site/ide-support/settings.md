# Extension Settings

## `reqnroll.json` compatibility

Reqnroll IDE Support reads the same `reqnroll.json` schema as the existing
Reqnroll for Visual Studio extension — **no changes are needed** to an
existing config file. See the
[Reqnroll Configuration Reference](https://docs.reqnroll.net/latest/installation/configuration.html)
and the legacy extension's
[`ide` section reference](https://docs.reqnroll.net/latest/ide-integrations/visual-studio/settings.html)
for the full settings schema (editor/formatting behavior, traceability tag
links, binding discovery overrides, and so on) — it applies unchanged here.

## Per-IDE settings surface

Where you go to change a *host IDE* setting (as opposed to a Reqnroll
project setting in `reqnroll.json`) differs per IDE:

| IDE | Where |
|---|---|
| **Visual Studio** | Tools → Options → (Reqnroll-specific pages, mirroring the existing extension's Options pages) |
| **VS Code** | `.vscode/settings.json`, or the Settings UI, under the Reqnroll extension's contributed settings |
| **Rider** | Settings → Languages & Frameworks → Reqnroll (or the equivalent Reqnroll settings page) |

TODO(media): 📷 screenshot — the Visual Studio Options page.

TODO(media): 📷 screenshot — the VS Code settings UI, filtered to the
Reqnroll extension's contributed settings.
