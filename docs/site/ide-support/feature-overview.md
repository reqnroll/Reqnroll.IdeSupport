---
hide-toc: true
---

# Feature Overview

What works, per IDE — and where something needs a second look, exactly
what to expect and what to do about it.

| Symbol | Meaning |
|---|---|
| ✅ | Works |
| ⚠️ | Works, with a caveat — see the note |
| ❌ | Not available in this IDE — see the note for the alternative |

## Editing Features

| Feature | VS Code | Visual Studio | Rider |
|---|:---:|:---:|:---:|
| [Syntax Highlighting](editing-features/syntax-highlighting.md) | ✅ | ✅ | ✅ |
| [Diagnostics — Errors & Warnings](editing-features/diagnostics.md) | ✅ | ✅ | ✅ |
| [Keyword & Step Completion](editing-features/completion.md) | ✅ | ✅ | ✅ |
| [Document & Table Formatting](editing-features/formatting.md) | ✅ | ⚠️² | ✅ |
| [Comment / Uncomment](editing-features/comment-uncomment.md) | ✅ | ✅ | ✅ |
| [Code Folding](editing-features/code-folding.md) | ✅ | ✅ | ✅ |
| [Document Outline](editing-features/document-outline.md) | ✅ | ❌³ | ✅ |
| [Defining Steps](editing-features/defining-steps.md) | ✅ | ✅ | ⚠️¹ |
| [Rename Step](editing-features/rename-step.md) | ⚠️⁴ | ✅ | ✅ |
| [Code Lens — Step Usage Counts](editing-features/code-lens.md) | ✅ | ✅ | ✅ |
| [Code Lens — Hook Matches](editing-features/code-lens.md) | ✅ | ✅ | ✅ |
| [Inlay Hints — Bound Step Info](editing-features/inlay-hints.md) | ✅ | ✅ | ✅ |

## Navigation Features

```{admonition} Also surfaced via Code Lens
:class: note

Find Step Definition Usages and Hook Navigation both also show up
passively while you're reading code, via [Code Lens](editing-features/code-lens.md) —
a usage count above a step binding, or a hook-match count above a
`Feature:`/`Scenario:` line, that you can click straight through to the
same result these commands produce. Code Lens is listed under Editing
Features since it's something you see unprompted, not something you
invoke — but if you're looking for one of these two, Code Lens is often
the faster path since you don't have to invoke anything.
```

| Feature | VS Code | Visual Studio | Rider |
|---|:---:|:---:|:---:|
| [Go to Step Definition](navigation-features/go-to-definition.md) | ✅ | ✅ | ✅ |
| [Find Step Definition Usages](navigation-features/find-usages.md) | ✅ | ⚠️⁵ | ✅ |
| [Find Unused Step Definitions](navigation-features/find-unused.md) | ✅ | ✅ | ✅ |
| [Hook Navigation ("Go to Hooks")](navigation-features/hook-navigation.md) | ✅ | ✅ | ✅ |

## Project Setup

| Feature | VS Code | Visual Studio | Rider |
|---|:---:|:---:|:---:|
| [New Project / Item Templates](new-project-templates.md) | ❌⁶ | ✅ | ❌⁶ |

---

¹ Not yet confirmed against a live Rider session — likely works via the
IntelliJ platform's generic support (the same mechanism diagnostics and
completion use), but hasn't been manually verified. Tracked in
[#437](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/437). If you
try it in Rider, that issue is the place to report what you find.

² Visual Studio ships its own built-in Gherkin formatting service, which
can override on-type table formatting. If table alignment doesn't seem to
be happening as you type, see
[Document & Table Formatting](editing-features/formatting.md) for the fix.

³ Visual Studio's native Document Outline window doesn't support
`.feature` files at all — a VS platform limitation, not something we can
configure around. See [Document Outline](editing-features/document-outline.md).

⁴ VS Code's rename doesn't yet support disambiguating a step bound to more
than one candidate binding. See [Rename Step](editing-features/rename-step.md)
for the workaround. Rider and Visual Studio both handle disambiguation
directly.

⁵ Visual Studio's native **Find All References** (Shift+F12) does not
route to Reqnroll bindings — use the dedicated **Find Step Usages** command
instead. See [Find Step Definition Usages](navigation-features/find-usages.md)
for exactly where to find it.

⁶ VS Code and Rider don't have an equivalent project wizard; use snippets
(VS Code) or live templates (Rider) instead — see
[New Project / Item Templates](new-project-templates.md).

```{admonition} Preview status
:class: note

Some features above shipped after this page was last reviewed for accuracy
against the running extensions — if something here looks wrong or out of
date, please [file an issue](https://github.com/reqnroll/Reqnroll.IdeSupport/issues).
```
