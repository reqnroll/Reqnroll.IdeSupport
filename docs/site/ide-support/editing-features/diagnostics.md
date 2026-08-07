# Diagnostics — Errors & Warnings

Steps in a `.feature` file with no matching step definition are underlined
with a warning squiggle and listed in your IDE's Error List / Problems
panel. Structural errors — a missing `Feature:` header, invalid tag syntax,
and similar parse errors — are underlined with a red error squiggle instead,
so you can tell "not written correctly" apart from "no binding exists yet"
at a glance.

Diagnostics refresh live as you type, and whenever step bindings change (on
a C# file save or a build) — no need to reopen the file to see an unmatched
step turn green once you add the matching binding.

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

![](diagnostics/diagnostics-vs-squiggle.png)

TODO(media): 🎬 gif (optional) — the squiggle disappearing live as a
matching binding is added. Nice to have; the screenshot above already
conveys the static state on its own.
**Target:** `diagnostics/diagnostics-vs-fix.gif`
```

```{tab-item} VS Code
:sync: vscode

![](diagnostics/diagnostics-vscode-squiggle.png)

TODO(media): 🎬 gif (optional) — the squiggle disappearing live as a
matching binding is added. Nice to have; the screenshot above already
conveys the static state on its own.
**Target:** `diagnostics/diagnostics-vscode-fix.gif`
```

```{tab-item} Rider
:sync: rider

![](diagnostics/diagnostics-rider-squiggle.png)

TODO(media): 🎬 gif (optional) — the squiggle disappearing live as a
matching binding is added. Nice to have; the screenshot above already
conveys the static state on its own.
**Target:** `diagnostics/diagnostics-rider-fix.gif`
```

:::
