# Syntax Highlighting

Keywords (`Feature:`, `Scenario:`, `Given`, `When`, `Then`, `And`, `But`),
step text, bound step argument text, tags (`@tag`), doc strings, data table
headers, data table cell content, and comments each render in distinct
colors, matching your IDE's color theme. Colors update as you type — no
save required. Steps with no matching binding render in a distinct
"undefined step" color once a binding registry is available.

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

TODO(media): 📷 screenshot showing colored Gherkin keywords/steps in
Visual Studio, with a matched (bound) step and an unmatched step side by
side so the color difference is visible.
**Target:** `syntax-highlighting/vs.png`
```

```{tab-item} VS Code
:sync: vscode

TODO(media): 📷 screenshot showing colored Gherkin keywords/steps in
VS Code, with a matched (bound) step and an unmatched step side by side so
the color difference is visible.
**Target:** `syntax-highlighting/vscode.png`
```

```{tab-item} Rider
:sync: rider

TODO(media): 📷 screenshot showing colored Gherkin keywords/steps in
Rider, with a matched (bound) step and an unmatched step side by side so
the color difference is visible.
**Target:** `syntax-highlighting/rider.png`
```

:::

## Non-English (dialect) keywords

Highlighting works the same for non-English Gherkin dialects (e.g. German,
French, Dutch) — the active dialect is read from the project's
`reqnroll.json` (default: `en`). See
[Feature Language](https://docs.reqnroll.net/latest/gherkin/feature-language.html)
in the main Reqnroll docs for the full list of supported languages and how
to set one.
