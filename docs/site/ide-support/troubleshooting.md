# Troubleshooting / FAQ

## Can I have both extensions installed at once?

Both **can** be installed side by side — installing one doesn't remove the
other. But running both **enabled** at the same time is **not a supported
configuration**: with both active, you'll get duplicate/conflicting
behavior (e.g. two sets of diagnostics, two CodeLens annotations) for the
same `.feature` files.

If you have both installed, disable one: **Extensions → Manage
Extensions**, select the extension you're not using, and click
**Disable**. See
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
