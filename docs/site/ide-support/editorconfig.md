# Gherkin Formatting with EditorConfig

[EditorConfig](https://editorconfig.org/) settings for `*.feature` files
work the same way, with the same setting names, as they do with the
existing Reqnroll for Visual Studio extension — see that extension's
[EditorConfig reference](https://docs.reqnroll.net/latest/ide-integrations/visual-studio/editorconfig.html)
for the full supported-settings table (`gherkin_indent_*`,
`gherkin_table_cell_*`, etc.) and a sample `.editorconfig` section. Those
settings apply unchanged to [Document & Table Formatting](editing-features/formatting.md)
in Visual Studio, VS Code, and Rider alike — no per-IDE variant of the
`.editorconfig` keys is needed.

```{note}
The Gherkin file format supports non-ASCII characters only in UTF-8. Set
`charset = utf-8` for `*.feature` files in your `.editorconfig` to ensure
they're saved correctly.
```
