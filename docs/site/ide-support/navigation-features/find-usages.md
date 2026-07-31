# Find Step Definition Usages

Invoking **Find All References** on a C# step binding method (a method
decorated with `[Given]`, `[When]`, or `[Then]`) finds every `.feature`
step that matches that binding and lists them in your IDE's references
panel. This is the inverse of [Go to Step Definition](go-to-definition.md).

TODO(media): 🎬 gif — invoking Find Usages from a `.cs` binding method and
seeing results across `.feature` files.

TODO(media): 🎬 gif — Rider's results, captured separately; Rider's find
usages includes the Feature/Rule name for Rule-nested scenarios and its
results popup differs visually from VS/VS Code's.

```{admonition} Visual Studio — native "Find All References" doesn't reach this
:class: warning

Visual Studio's built-in **Find All References** (Shift+F12) does not route
to Reqnroll bindings. Use the entry point documented above instead — invoke
Find Usages from the step binding method specifically, not through VS's
generic Roslyn-only command.
```
