# Defining Steps

When a step in a `.feature` file has no matching binding, a quick-fix /
code action **"Define missing steps"** appears (the lightbulb in VS Code
and Visual Studio, Alt+Enter in Rider). Activating it generates stub
binding methods — in a new or existing step definition file — with method
signatures and parameter types inferred from the step text.

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

TODO(media): 🎬 gif — invoking the quick-fix on an unmatched step in
Visual Studio and seeing the generated binding method.
**Target:** `defining-steps/vs.gif`
```

```{tab-item} VS Code
:sync: vscode

TODO(media): 🎬 gif — invoking the quick-fix on an unmatched step in
VS Code and seeing the generated binding method.
**Target:** `defining-steps/vscode.gif`
```

```{tab-item} Rider
:sync: rider

TODO(media): 🎬 gif — invoking the quick-fix in Rider, once
[#414](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/414)-style
verification confirms it's actually wired up — see the note below.
**Target:** `defining-steps/rider.gif`
```

:::

```{admonition} Rider — verify before documenting as supported
:class: note

No Rider-specific code-action source was found for this feature as of this
writing. It should be manually verified in a live Rider session whether
this auto-negotiates via the platform's generic LSP code-action support
(the same way diagnostics does) or isn't wired up yet, before this page
states Rider support definitively.
```

See also [New Project / Item Templates](../new-project-templates.md) for
scaffolding a brand-new `.feature` file or step definitions class (as
opposed to a single missing step).
