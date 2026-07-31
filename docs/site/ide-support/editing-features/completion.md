# Keyword & Step Completion

## Keyword completion

Typing at the start of a line in a scenario offers completions for the
keywords valid at that point (`Given`, `When`, `Then`, `And`, `But`,
`Scenario:`, `Feature:`, and so on). Completions are context-sensitive —
`Examples:` is only offered inside a Scenario Outline, `Background:` only
at feature level.

## Step completion

Typing a step line after a keyword offers completions for existing step
binding patterns, with parameter placeholders shown distinctly, and inserts
the full step text on selection.

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

TODO(media): 🎬 gif — typing a keyword or step, the completion popup
appearing, and selecting an item. Completion is a live typing/filtering
interaction that a screenshot can't convey the trigger/filter behavior of.
```

```{tab-item} VS Code
:sync: vscode

TODO(media): 🎬 gif — typing a keyword or step, the completion popup
appearing, and selecting an item.
```

```{tab-item} Rider
:sync: rider

TODO(media): 🎬 gif — typing a keyword or step, the completion popup
appearing, and selecting an item — capture only once
[#414](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/414) confirms
Rider completion actually works; see the admonition below.
```

:::

```{admonition} Rider completion — verify before relying on this page
:class: warning

No Rider-specific completion source code was found in the plugin as of this
writing — it may rely on the IDE platform's generic LSP completion
auto-negotiation (the same mechanism diagnostics use), but this should be
manually confirmed in a live Rider session before this page states Rider
completion as fully supported. Tracked in
[#414](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/414).
```

## Non-English (dialect) keywords

Completions are sourced from the active Gherkin dialect in the project's
`reqnroll.json`. A project configured with `"language": "de"` offers
`Gegeben`, `Wenn`, `Dann` rather than `Given`, `When`, `Then`.
