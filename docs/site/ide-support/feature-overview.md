---
hide-toc: true
---

# Feature Overview

Every feature below is available in all three IDEs unless the support
matrix says otherwise. Ratings:

| Rating | Meaning |
|---|---|
| ✅ | Works fully, without any special setup |
| ⚠️ | Works, but needs a small IDE-side setting enabled |
| 🔧 | Works via dedicated Reqnroll-specific integration code in that IDE |
| ❌ | Not applicable to that IDE (an equivalent native mechanism is used instead) |

| Feature | VS Code | Visual Studio | Rider |
|---|:---:|:---:|:---:|
| [Syntax Highlighting](editing-features/syntax-highlighting.md) | ✅ | ⚠️ | ✅ |
| [Diagnostics — Errors & Warnings](editing-features/diagnostics.md) | ✅ | ✅ | ✅ |
| [Keyword & Step Completion](editing-features/completion.md) | ✅ | ✅ | ✅ |
| [Document & Table Formatting](editing-features/formatting.md) | ✅ | ✅ | ⚠️ |
| [Comment / Uncomment](editing-features/comment-uncomment.md) | 🔧 | 🔧 | 🔧 |
| [Code Folding](editing-features/code-folding.md) | ✅ | ✅ | 🔧 |
| [Document Outline](editing-features/document-outline.md) | ✅ | ⚠️ | 🔧 |
| [Code Lens — Step Usage Counts](editing-features/code-lens.md) | ✅ | 🔧 | ⚠️ |
| [Code Lens — Hook Matches](editing-features/code-lens.md) | ✅ | 🔧 | ✅ |
| [Inlay Hints — Bound Step Info](editing-features/inlay-hints.md) | ✅ | ✅ | ✅ |
| [Rename Step](editing-features/rename-step.md) | ✅¹ | ✅ | ✅ |
| [Go to Step Definition](navigation-features/go-to-definition.md) | ✅ | ✅ | ✅ |
| [Find Step Definition Usages](navigation-features/find-usages.md) | ⚠️ | ⚠️ | ⚠️ |
| [Find Unused Step Definitions](navigation-features/find-unused.md) | 🔧 | 🔧 | 🔧 |
| [Hook Navigation ("Go to Hooks")](navigation-features/hook-navigation.md) | 🔧 | 🔧 | 🔧 |
| [Step Definition Scaffolding](defining-steps.md) | ✅ | ✅ | ✅ |
| [New Project / Item Templates](new-project-templates.md) | ❌² | 🔧 | ❌² |

¹ VS Code's rename does not yet support disambiguating a step bound to more
than one candidate binding — see [Rename Step](editing-features/rename-step.md)
for the workaround. Rider and Visual Studio both handle disambiguation.

² VS Code and Rider don't have an equivalent wizard; use snippets / live
templates instead, per [New Project / Item Templates](new-project-templates.md).

```{admonition} Preview status
:class: note

Some features above shipped after this page was last reviewed for accuracy
against the running extensions — if something here looks wrong or out of
date, please [file an issue](https://github.com/reqnroll/Reqnroll.IdeSupport/issues).
```
