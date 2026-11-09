# Hook Navigation

**Find Hooks**, invoked from a `.feature` file, lists the hook bindings in
scope at the cursor position, filtered by the tags and `[Scope]`
expressions that apply there:

* From a `Feature:` or `Scenario:` line: shows `[BeforeFeature]`/`[AfterFeature]`
  or `[BeforeScenario]`/`[AfterScenario]` hooks that apply.
* From a step line: additionally shows `[BeforeStep]`/`[AfterStep]` and
  `[BeforeStepBlock]`/`[AfterStepBlock]` hooks.

Selecting an entry navigates to the C# hook method.

```{note}
Tag and `[Scope]` filters are only understood when written as string literals, and a hook's
`Order` only as an integer literal. A non-literal value, such as a constant, is dropped while you
edit the `.cs` file, so a hook can be listed where it does not apply. See
[Bindings that use constants or other non-literal arguments](../troubleshooting.md#bindings-that-use-constants-or-other-non-literal-arguments).
```

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

Place the cursor in a `.feature` file, then right-click → **Find Hooks**
(in the Reqnroll group of the code editor context menu).

![](hook-navigation/hook-navigation-vs.gif)
```

```{tab-item} VS Code
:sync: vscode

Place the cursor in a `.feature` file, then either:

- Right-click → **Reqnroll: Find Hooks**, or
- find **Reqnroll: Find Hooks** in the Command Palette.

No default keybinding.

![](hook-navigation/hook-navigation-vscode.gif)
```

```{tab-item} Rider
:sync: rider

Place the cursor in a `.feature` file, then right-click → **Reqnroll: Find Hooks**.

No default keybinding.

![](hook-navigation/hook-navigation-rider.gif)
```

:::

```{tip}
You don't always need to invoke this explicitly — [Code Lens](../editing-features/code-lens.md)
shows hook-match counts inline above `Feature:`/`Scenario:` lines while
you're reading the code, and clicking one opens the same picker filtered
to that lens's own hook set.
```

```{note}
If a `.feature` file is linked into more than one project, the hooks shown here always come from
the file's *home* project (wherever it physically lives on disk) — not necessarily the project
you opened it from. See [Troubleshooting](../troubleshooting.md#a-shared-feature-file-shows-hooks-diagnostics-or-highlighting-from-the-wrong-project).
```
