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

## Installing both extensions side by side

The Preview extension and the existing Reqnroll for Visual Studio extension
can both be installed at the same time — installing one does not disable or
remove the other. You might want the Preview extension installed alongside
the existing one to:

* try LSP-based navigation, refactoring, and CodeLens features ahead of
  general availability;
* compare behavior against the existing extension while migrating a large
  solution;
* report Preview-specific issues without losing your existing setup.

See [Troubleshooting / FAQ](../troubleshooting.md) for what to expect when
both extensions are active in the same solution.
