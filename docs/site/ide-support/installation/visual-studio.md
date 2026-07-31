# Install for Visual Studio

Reqnroll IDE Support for Visual Studio supports **Visual Studio 2022** and
**Visual Studio 2026**.

## Install via the Marketplace

1. In Visual Studio, go to **Extensions → Manage Extensions**.
2. Search for **"Reqnroll Extension for Visual Studio (Preview)"**.

```{admonition} Don't confuse the two extensions
:class: warning

The Marketplace search will also surface the existing, non-preview
**"Reqnroll.VisualStudio"** extension. They are separate listings — install
the one explicitly labeled **(Preview)** to get the LSP-based extension
described in these docs.
```

TODO(media): 📷 screenshot of the Marketplace/"Manage Extensions" search
results panel, with the Preview extension's listing visibly distinct from
the existing "Reqnroll.VisualStudio" entry.

3. Install, then restart Visual Studio when prompted.

## Only enable one at a time

The Preview extension and the existing Reqnroll for Visual Studio extension
can both be **installed** at the same time — installing one does not
remove the other. But running both **enabled** together is **not a
supported configuration**: you'll get duplicate/conflicting behavior (e.g.
two sets of diagnostics, two CodeLens annotations) for the same `.feature`
files.

If you install the Preview extension to try it alongside your existing
setup, go to **Extensions → Manage Extensions** and **disable** whichever
one you're not actively using. See [Troubleshooting / FAQ](../troubleshooting.md#can-i-have-both-extensions-installed-at-once)
for more.
