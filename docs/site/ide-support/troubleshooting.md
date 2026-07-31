# Troubleshooting / FAQ

## Can I have both extensions installed at once?

Yes — Reqnroll IDE Support (Preview) and the existing Reqnroll for Visual
Studio extension can be installed side by side; installing or using one
does not disable the other. See
[Install for Visual Studio](installation/visual-studio.md) for how to tell
the two listings apart in the Marketplace.

## Known per-IDE limitations

* **Visual Studio** — the native Document Outline window does not show
  `.feature` file structure. See [Document Outline](editing-features/document-outline.md).
* **Visual Studio** — native "Find All References" (Shift+F12) does not
  route to Reqnroll step bindings; use the dedicated entry point instead.
  See [Find Step Definition Usages](navigation-features/find-usages.md).
* **VS Code** — Rename doesn't yet support disambiguating a step bound to
  more than one candidate binding. See [Rename Step](editing-features/rename-step.md)
  for the workaround (Rider and Visual Studio both handle this case).

## How do I report a bug?

File an issue on the
[Reqnroll.IdeSupport repository](https://github.com/reqnroll/Reqnroll.IdeSupport/issues),
including your IDE and version, the extension version, and — if possible —
the LSP server log (see your IDE's output/log panel for the Reqnroll
language server).

## Where does telemetry data go?

TODO: document the extension's telemetry policy here once finalized (what
is/isn't collected, and how to opt out) — cross-reference the relevant
privacy documentation once published.
