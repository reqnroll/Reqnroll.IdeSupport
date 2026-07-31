# Syntax Highlighting

Keywords (`Feature:`, `Scenario:`, `Given`, `When`, `Then`, `And`, `But`),
step text, bound step argument text, tags (`@tag`), doc strings, data table
headers, data table cell content, and comments each render in distinct
colors, matching your IDE's color theme. Colors update as you type — no
save required. Steps with no matching binding render in a distinct
"undefined step" color once a binding registry is available.

TODO(media): 📷 screenshot showing colored Gherkin keywords/steps, with a
matched (bound) step and an unmatched step side by side so the color
difference is visible.

## Non-English (dialect) keywords

Highlighting works the same for non-English Gherkin dialects (e.g. German,
French, Dutch) — the active dialect is read from the project's
`reqnroll.json` (default: `en`).
