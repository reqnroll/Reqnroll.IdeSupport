# Feature Designs

One document per feature area. Each follows the shape described in the [documentation guide](../README.md#shape-of-a-design-document):
purpose, overview diagram, flows, glossary, key classes, behaviour and contracts, pitfalls, limitations and testing. For the
cross-IDE view of how each feature is delivered, see the matrix in [../architecture/ide-clients.md](../architecture/ide-clients.md).

| ID | Feature | Document |
|---|---|---|
| F1 | Syntax highlighting | [syntax-highlighting.md](syntax-highlighting.md) |
| F2 | Binding discovery | [../architecture/binding-discovery.md](../architecture/binding-discovery.md) |
| F3, F4, F27 | Diagnostics: step mismatches, parse errors, C# binding validation | [diagnostics.md](diagnostics.md) |
| F5, F14, F15 | Go to step definition, find step usages, find unused step definitions | [step-navigation.md](step-navigation.md) |
| F6 | Define steps (scaffolding) | [define-steps.md](define-steps.md) |
| F7, F8 | Keyword, step and tag completion | [completion.md](completion.md) |
| F9, F10 | Document outline and code folding | [outline-and-folding.md](outline-and-folding.md) |
| F11, F12, F13 | Document and table formatting, comment toggle | [formatting-and-comments.md](formatting-and-comments.md) |
| F16 | Step rename | [rename.md](rename.md) |
| F17, F18, F24, F25 | Go to hooks, step-usage and hook CodeLenses | [hooks-and-lenses.md](hooks-and-lenses.md) |
| F19, F20 | New project/item wizards, installation and upgrade experience | [../Onboarding-And-Scaffolding.md](../Onboarding-And-Scaffolding.md) |
| F23 | Inlay hints | [inlay-hints.md](inlay-hints.md) |
| F26 | Test runner integration: Run, results, failed-step mark | [../Test-Runner-Integration-Design.md](../Test-Runner-Integration-Design.md), [../Test-Outcomes-Pipeline.md](../Test-Outcomes-Pipeline.md) |
| F28 | Clickable tags (traceability links) | [clickable-tags.md](clickable-tags.md) |
| (infrastructure) | Linked files and project membership | [../architecture/workspace-and-membership.md](../architecture/workspace-and-membership.md) |

F21 and F22 are not used. Debug support for feature files is not built; see open question O6 in the
[register](../LSP-IDE-Support-Open-Questions.md#1-open-questions).
