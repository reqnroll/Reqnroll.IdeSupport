# Installation

:::{tab-set}

````{tab-item} Visual Studio
:sync: vs

Reqnroll IDE Support for Visual Studio supports **Visual Studio 2022** and
**Visual Studio 2026**.

**Install via the Marketplace:**

1. In Visual Studio, go to **Extensions → Manage Extensions**.
2. Search for **"Reqnroll Extension for Visual Studio (Preview)"**.
3. Install, then restart Visual Studio when prompted.

TODO(media): 📷 screenshot of the Marketplace/"Manage Extensions" search
results panel, with the Preview extension's listing visibly distinct from
the existing "Reqnroll.VisualStudio" entry.
**Target:** `index/index-vs-marketplace.png`

```{admonition} Don't confuse the two extensions
:class: warning

The Marketplace search will also surface the existing, non-preview
**"Reqnroll.VisualStudio"** extension. They are separate listings — install
the one explicitly labeled **(Preview)** to get the LSP-based extension
described in these docs.
```

**Only enable one at a time.** The Preview extension and the existing
Reqnroll for Visual Studio extension can both be **installed** at the same
time — installing one does not remove the other. But running both
**enabled** together is **not a supported configuration**: you'll get
duplicate/conflicting behavior (e.g. two sets of diagnostics, two CodeLens
annotations) for the same `.feature` files.

If you install the Preview extension to try it alongside your existing
setup, go to **Extensions → Manage Extensions** and **disable** whichever
one you're not actively using. See [Troubleshooting / FAQ](../troubleshooting.md#can-i-have-both-extensions-installed-at-once)
for more.
````

````{tab-item} VS Code
:sync: vscode

1. Open the Extensions view (`Ctrl+Shift+X` / `Cmd+Shift+X`).
2. Search for **"Reqnroll"** and install the Reqnroll extension.

TODO(media): 📷 screenshot of the extension listing in the VS Code
Marketplace panel.
**Target:** `index/index-vscode-marketplace.png`

```{admonition} Replaces the "Cucumber" extension recommendation
:class: note

Earlier VS Code setup guidance recommended installing the generic
**Cucumber (Gherkin) Full Support** extension for `.feature` file syntax
highlighting and navigation. With the Reqnroll extension installed, that
recommendation no longer applies — the Reqnroll extension covers syntax
highlighting, diagnostics, completion, navigation, and refactoring for
Gherkin files directly, so a third-party substitute is unnecessary.
```

**`.vscode/settings.json` — no glob configuration required.** Previously,
VS Code setup required manually configuring which files belong to which
project via `cucumber.glue` / `cucumber.features` globs in
`.vscode/settings.json`. With the Reqnroll extension, **this manual
configuration is no longer required**: the underlying language server
auto-discovers project membership directly from your project files. If you
have existing `cucumber.glue` / `cucumber.features` settings from a prior
setup, they can be removed once you've confirmed navigation and completion
work as expected with the Reqnroll extension active.
````

````{tab-item} Rider
:sync: rider

1. In Rider, go to **Settings → Plugins → Marketplace**.
2. Search for **"Reqnroll"** and install the plugin.
3. Restart Rider when prompted.

TODO(media): 📷 screenshot of the Marketplace search results panel showing
the Reqnroll plugin listing.
**Target:** `index/index-rider-marketplace.png`

```{admonition} Supported Rider version
:class: note

Verified against Rider 2024.3.5. The IntelliJ Platform LSP APIs this plugin
depends on are still marked `@Experimental` upstream, so behavior can shift
on Rider SDK updates.
```

**Confirm the server started.** After restarting, open a `.feature` file
and check the Language Servers status widget for a **"Reqnroll"** entry
confirming the server started.

TODO(media): 📷 screenshot of the Rider Language Servers status widget
showing the Reqnroll entry.
**Target:** `index/index-rider-status-widget.png`
````

:::
