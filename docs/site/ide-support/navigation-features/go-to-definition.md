# Go to Step Definition

With the cursor on a step in a `.feature` file, **Go to Definition** (F12,
or Ctrl+Click / Cmd+Click) jumps to the matching `[Given]`/`[When]`/`[Then]`
method in the C# binding class. If more than one binding matches
(ambiguous), a picker lists the candidates.

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

![](go-to-definition/go-to-definition-vs.gif)
```

```{tab-item} VS Code
:sync: vscode

TODO(media): 🎬 gif — cursor placed on a step, then jumping to the bound
C# method.
**Target:** `go-to-definition/go-to-definition-vscode.gif`
```

```{tab-item} Rider
:sync: rider

TODO(media): 🎬 gif — Rider's jump animation, captured separately; it looks
different from VS/VS Code's.
**Target:** `go-to-definition/go-to-definition-rider.gif`
```

:::
