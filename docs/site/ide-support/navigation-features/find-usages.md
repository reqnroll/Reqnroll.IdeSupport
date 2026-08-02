# Find Step Definition Usages

Invoking **Find Step Usages** on a C# step binding method (a method
decorated with `[Given]`, `[When]`, or `[Then]`) finds every `.feature`
step that matches that binding and lists them in your IDE's references
panel. This is the inverse of [Go to Step Definition](go-to-definition.md).

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

Place the cursor in (or right-click) the binding method in a `.cs` file,
then either:

- Right-click → **Find Step Usages** (in the code editor context menu,
  next to "Find All References"), or
- **Extensions → Reqnroll → Find Step Usages**.

Results open in the standard Find All References window.

TODO(media): 🎬 gif — invoking Find Usages from a `.cs` binding method and
seeing results across `.feature` files.
**Target:** `find-usages/find-usages-vs.gif`
```

```{tab-item} VS Code
:sync: vscode

Place the cursor in the binding method in a `.cs` file, then either:

- Right-click → **Reqnroll: Find Step Usages**, or
- Command Palette → **Reqnroll: Find Step Usages**.

Results appear in a Quick Pick list — selecting an entry jumps to that
step in its `.feature` file.

TODO(media): 🎬 gif — invoking Find Usages from a `.cs` binding method and
seeing results across `.feature` files.
**Target:** `find-usages/find-usages-vscode.gif`
```

```{tab-item} Rider
:sync: rider

Place the cursor in the binding method in a `.cs` file, then either:

- Right-click → **Find Step Usages**, or
- **Tools → Reqnroll → Find Step Usages**.

TODO(media): 🎬 gif — Rider's results, captured separately; Rider's find
usages includes the Feature/Rule name for Rule-nested scenarios and its
results popup differs visually from VS/VS Code's.
**Target:** `find-usages/find-usages-rider.gif`
```

:::

```{admonition} Visual Studio — native "Find All References" doesn't reach this
:class: warning

Visual Studio's built-in **Find All References** (Shift+F12) does not route
to Reqnroll bindings — it only searches C# symbol references. Use
**Find Step Usages** instead (see the Visual Studio tab above), not the
native command.
```

```{tip}
You don't always need to invoke this explicitly — [Code Lens](../editing-features/code-lens.md)
shows the same usage count inline above every step binding method while
you're reading the code, and clicking it opens these same results.
```
