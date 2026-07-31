# Document & Table Formatting

## Format Document

Formatting the whole document (Shift+Alt+F, or your IDE's equivalent
shortcut) re-indents the entire `.feature` file: consistent indentation per
nesting level, normalized spacing around keywords, and blank lines between
scenarios. Formatting rules are read from `.editorconfig` — see
[Gherkin Formatting with EditorConfig](../editorconfig.md) for the full
settings reference.

## Table formatting

Typing `|` or pressing Enter inside a Gherkin data table or `Examples:`
table pads the columns so the pipes stay aligned as you type. A table can
also be re-aligned by running Format Document. A row missing its trailing
`|` gets one appended automatically.

TODO(media): 🎬 gif — before/after auto-format on save or on-type, and the
table column-alignment behavior as you type. This is a "watch the text
reflow" feature; screenshots lose the point.

```{admonition} Visual Studio 2022 — built-in Gherkin formatter conflict
:class: warning

VS 2022 ships its own built-in Gherkin formatting service, which can
override this extension's on-type table formatting. If table alignment
doesn't seem to be happening as you type, check whether the built-in
service is intercepting the on-type formatting request.
```

```{admonition} Rider table formatting
:class: note

Rider has on-type table-column realignment implemented. Full-document
format-on-save parity with VS/VS Code should be confirmed before this page
claims it as fully complete for Rider.
```
