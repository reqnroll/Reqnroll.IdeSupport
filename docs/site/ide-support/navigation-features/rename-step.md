# Rename Step

Renaming a step's text — from either the `.feature` file step line or the
C# `[Given("...")]`/`[When("...")]`/`[Then("...")]` attribute string —
updates every occurrence across the workspace: the attribute string in the
binding class, and every matching step in every `.feature` file.

TODO(media): 🎬 gif — renaming a step definition and watching every
matching `.feature` step update.

```{admonition} VS Code — known limitation with ambiguous bindings
:class: warning

If a step is bound to more than one candidate attribute (multi-attribute
disambiguation), VS Code's rename does not yet support choosing which one
you mean. **Workaround:** place your cursor directly in the specific
attribute string you want to rename, rather than on the step in the
`.feature` file, before invoking rename.

This limitation is specific to VS Code. Rider and Visual Studio both
implement rename **with** disambiguation — a picker lets you choose which
candidate binding to rename when a step matches more than one.
```
