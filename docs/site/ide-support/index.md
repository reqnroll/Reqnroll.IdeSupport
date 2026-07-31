---
hide-toc: true
---

# Reqnroll IDE Support (Preview)

**Reqnroll IDE Support** is a single [Language Server Protocol](https://microsoft.github.io/language-server-protocol/)
(LSP) server behind three thin IDE clients — Visual Studio, Visual Studio
Code, and JetBrains Rider — providing consistent Gherkin editing, navigation,
and refactoring features across all three, instead of maintaining separate
per-IDE implementations of the same logic.

```{admonition} Preview status
:class: important

This extension is currently in **Preview**. It can be installed alongside
the existing [Reqnroll for Visual Studio](https://docs.reqnroll.net/latest/ide-integrations/visual-studio/index.html)
extension — installing one does not remove the other, and there is no
automatic migration between them — but running both **enabled** at once is
not a supported configuration; see
[Troubleshooting / FAQ](troubleshooting.md#can-i-have-both-extensions-installed-at-once).
It is intended to eventually replace the legacy Visual Studio extension
once feature parity and stability criteria are met; those criteria will be
published here once finalized.
```

* [Installation](installation/index.md) — install the extension for your IDE
* [Upgrading](upgrading.md) — what happens on first install vs. an upgrade
* [Feature Overview](feature-overview.md) — every feature, with a per-IDE support matrix
* [Editing Features](editing-features/index.md) — syntax highlighting, diagnostics, completion, formatting
* [Navigation Features](navigation-features/index.md) — jump between steps, bindings, and hooks
* [Defining Steps](defining-steps.md) — scaffolding a missing step definition
* [New Project / Item Templates](new-project-templates.md) — Visual Studio project/item wizards
* [Extension Settings](settings.md) — configure the extension per IDE
* [Gherkin Formatting with EditorConfig](editorconfig.md) — consistent formatting via `.editorconfig`
* [Troubleshooting / FAQ](troubleshooting.md) — known per-IDE limitations, coexistence, reporting bugs

```{toctree}
:hidden:

installation/index
upgrading
feature-overview
editing-features/index
navigation-features/index
defining-steps
new-project-templates
settings
editorconfig
troubleshooting
```
