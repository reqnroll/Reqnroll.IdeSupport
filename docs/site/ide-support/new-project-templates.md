# New Project / Item Templates

In Visual Studio, **New Project** offers a Reqnroll project template with a
test framework picker (NUnit, xUnit, MSTest). **Add New Item** offers a
blank `.feature` file template and a step definitions class template.

TODO(media): 📷 screenshot — the New Project dialog with the Reqnroll
template and test-framework picker.
**Target:** `new-project-templates/new-project-templates-vs-new-project.png`

TODO(media): 📷 screenshot — the Add New Item templates.
**Target:** `new-project-templates/new-project-templates-vs-add-item.png`

```{admonition} Visual Studio only
:class: note

VS Code and Rider don't have an equivalent project wizard — use snippets
(VS Code) or live templates (Rider) as the equivalent entry point for
scaffolding a new `.feature` file or step definitions class. See
[Defining Steps](editing-features/defining-steps.md) for scaffolding a single missing step
binding, which works the same way across all three IDEs.
```
