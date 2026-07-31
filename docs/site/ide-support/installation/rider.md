# Install for Rider

The Rider plugin is functional and implements most of the same feature set
as the Visual Studio and VS Code clients (diagnostics, semantic tokens,
inlay hints, CodeLens/CodeVision, folding, comment toggle, on-type table
formatting, go-to-definition, find usages, find unused, go-to-hooks, and
rename with disambiguation). It has **not yet been published to the
JetBrains Marketplace**, so install it from a locally built or CI-built
plugin ZIP instead of searching the Marketplace.

```{admonition} Verified Rider version
:class: note

Verified against Rider 2024.3.5. The IntelliJ Platform LSP APIs this plugin
depends on are still marked `@Experimental` upstream, so behavior can shift
on Rider SDK updates.
```

## Build the plugin

From the `src/Rider` folder of the [Reqnroll.IdeSupport](https://github.com/reqnroll/Reqnroll.IdeSupport)
repository:

```bash
./gradlew buildPlugin
```

This produces a plugin ZIP under `build/distributions/`. (Alternatively,
download a CI-built artifact once one is published — check the repository's
Actions/Releases for a published build.)

## Install from disk

1. In Rider, go to **Settings → Plugins**.
2. Click the gear icon → **Install Plugin from Disk...**.
3. Select the ZIP produced above, then restart Rider when prompted.

TODO(media): 📷 screenshot of Settings → Plugins → gear icon →
"Install Plugin from Disk".

## Confirm the server started

After restarting, open a `.feature` file and check the Language Servers
status widget for a **"Reqnroll"** entry confirming the server started.

TODO(media): 📷 screenshot of the Rider Language Servers status widget
showing the Reqnroll entry.

```{admonition} Once published to the Marketplace
:class: note

This page should be rewritten to match the Marketplace-install pattern used
on the [Visual Studio](visual-studio.md) and [VS Code](vscode.md) pages once
the plugin is available there.
```
