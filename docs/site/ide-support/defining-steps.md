---
hide-toc: true
---

# Defining Steps

## Step definition scaffolding

When a step in a `.feature` file has no matching binding, a quick-fix /
code action **"Define missing steps"** appears (the lightbulb in VS Code
and Visual Studio, Alt+Enter in Rider). Activating it generates stub
binding methods — in a new or existing step definition file — with method
signatures and parameter types inferred from the step text.

TODO(media): 🎬 gif — invoking the quick-fix on an unmatched step and
seeing the generated binding method.

```{admonition} Rider — verify before documenting as supported
:class: note

No Rider-specific code-action source was found for this feature as of this
writing. It should be manually verified in a live Rider session whether
this auto-negotiates via the platform's generic LSP code-action support
(the same way diagnostics does) or isn't wired up yet, before this page
states Rider support definitively.
```

## New Project / Item templates (Visual Studio only)

In Visual Studio, **New Project** offers a Reqnroll project template with a
test framework picker (NUnit, xUnit, MSTest). **Add New Item** offers a
blank `.feature` file template and a step definitions class template.

TODO(media): 📷 screenshot — the New Project dialog with the Reqnroll
template and test-framework picker.

TODO(media): 📷 screenshot — the Add New Item templates.

VS Code and Rider don't have an equivalent project wizard — use snippets
(VS Code) or live templates (Rider) as the equivalent entry point for
scaffolding a new `.feature` file or step definitions class.
