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

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

**Tools → Options →** the Reqnroll-specific pages, mirroring the existing
extension's Options pages.

TODO(media): 📷 screenshot — the Visual Studio Options page.
**Target:** `settings/vs.png`
```

```{tab-item} VS Code
:sync: vscode

`.vscode/settings.json`, or the Settings UI, under the Reqnroll
extension's contributed settings.

TODO(media): 📷 screenshot — the VS Code settings UI, filtered to the
Reqnroll extension's contributed settings.
**Target:** `settings/vscode.png`
```

```{tab-item} Rider
:sync: rider

**Settings → Languages & Frameworks → Reqnroll** (or the equivalent
Reqnroll settings page).

TODO(media): 📷 screenshot — the Rider settings page for Reqnroll.
**Target:** `settings/rider.png`
```

:::
