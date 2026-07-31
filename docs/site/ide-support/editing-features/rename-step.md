# Rename Step

Renaming a step's text — from either the `.feature` file step line or the
C# `[Given("...")]`/`[When("...")]`/`[Then("...")]` attribute string —
updates every occurrence across the workspace: the attribute string in the
binding class, and every matching step in every `.feature` file.

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

Place the cursor on the step (in a `.feature` file) or the attribute string
(in a `.cs` file) and invoke the standard **Rename** gesture (F2). This
handles the common, unambiguous case directly.

If a binding method has more than one candidate attribute (see the
disambiguation note below), use **Extensions → Reqnroll → Rename Step**
instead — it opens a dialog with a picker to choose which binding to
rename.

TODO(media): 🎬 gif — renaming a step definition in Visual Studio and
watching every matching `.feature` step update.
```

```{tab-item} VS Code
:sync: vscode

Place the cursor on the step (in a `.feature` file) and press **F2**, or
on the attribute string (in a `.cs` file) and use the standard **Rename
Symbol** gesture. On the `.feature` side, F2 is also available via
right-click → **Reqnroll: Rename Step** or the Command Palette.

TODO(media): 🎬 gif — renaming a step definition in VS Code and watching
every matching `.feature` step update.
```

```{tab-item} Rider
:sync: rider

- **On the `.feature` step line:** press **Shift+F6**, or right-click →
  **Rename Step**.
- **On the C# attribute:** right-click → **Rename Step (Reqnroll)** — a
  distinct context-menu entry from Rider's native "Rename", since native
  Shift+F6 stays bound to ordinary C# symbol rename here.

Both surfaces handle disambiguation via a picker when the binding has more
than one candidate attribute.

TODO(media): 🎬 gif — renaming a step definition in Rider, capturing its
disambiguation picker specifically (see the note below) — not just a port
of the VS/VS Code capture.
```

:::

```{admonition} VS Code — known limitation with ambiguous bindings
:class: warning

If a step is bound to more than one candidate attribute (multi-attribute
disambiguation), VS Code's rename does not yet support choosing which one
you mean. **Workaround:** place your cursor directly in the specific
attribute string you want to rename, rather than on the step in the
`.feature` file, before invoking rename.

This limitation is specific to VS Code. Rider and Visual Studio both
implement rename **with** disambiguation — a picker lets you choose which
candidate binding to rename when a step matches more than one (see the
Visual Studio and Rider tabs above for how to reach that picker).
```
