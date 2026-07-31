# Install for VS Code

1. Open the Extensions view (`Ctrl+Shift+X` / `Cmd+Shift+X`).
2. Search for **"Reqnroll"** and install the Reqnroll extension.

TODO(media): 📷 screenshot of the extension listing in the VS Code
Marketplace panel.

```{admonition} Replaces the "Cucumber" extension recommendation
:class: note

Earlier VS Code setup guidance recommended installing the generic
**Cucumber (Gherkin) Full Support** extension for `.feature` file syntax
highlighting and navigation. With the Reqnroll extension installed, that
recommendation no longer applies — the Reqnroll extension covers syntax
highlighting, diagnostics, completion, navigation, and refactoring for
Gherkin files directly, so a third-party substitute is unnecessary.
```

## `.vscode/settings.json` — no glob configuration required

Previously, VS Code setup required manually configuring which files belong
to which project via `cucumber.glue` / `cucumber.features` globs in
`.vscode/settings.json`. With the Reqnroll extension, **this manual
configuration is no longer required**: the underlying language server
auto-discovers project membership directly from your project files. If you
have existing `cucumber.glue` / `cucumber.features` settings from a prior
setup, they can be removed once you've confirmed navigation and completion
work as expected with the Reqnroll extension active.
