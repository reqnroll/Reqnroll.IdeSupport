# Documentation outline for docs.reqnroll.net — LSP-based IDE support (revised)

Revision of the outline originally posted to
[issue #63](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/63).
Structure and section numbering are unchanged; this revision adds the two
CodeLens features (F24, F25) that shipped after the original outline was
written and were missing from §5h, and folds them into the page structure
actually scaffolded under `docs/site/ide-support/`.

Legend: **📝 text** = prose/code-block only · **📷 screenshot** = static
image(s) needed · **🎬 gif** = short animated capture needed.

---

## What changed from the original outline

- **§5h Code Lens** now covers three related lenses, not one:
  - F18 — step usage counts (C# side), already in the original outline.
  - **F24 (new)** — hook match counts by Feature/Scenario/Step, shown on the
    `.feature` side. Shipped via issues [#269](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/269)/[#372](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/372).
  - **F25 (new)** — hook match counts by hook binding, shown on the C# side
    (the reverse direction of F24). Shipped via issue [#373](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/373).
  - All three lens kinds are documented together on one page
    (`editing-features/code-lens.md`) since they're conceptually one
    feature family and a reader comparing them benefits from seeing them
    side by side, rather than split across three pages.
- Media needs added: F24/F25 need a screenshot of the native CodeLens
  rendering (VS Code / Visual Studio) alongside Rider's CodeVision
  rendering, since the two render visibly differently — same pattern
  already called out for F18.
- Everything else in the original outline (installation split three ways,
  the Upgrading page, Feature Overview matrix, Navigation/Editing/Defining
  Steps sections, Settings, EditorConfig, Troubleshooting) is unchanged in
  shape. See the original comment on issue #63 for the full per-page
  rationale — this file only documents the delta.

## Current page tree (as scaffolded)

```
ide-support/
├── index.md                         §1 Landing page
├── installation/
│   ├── index.md
│   ├── visual-studio.md             §2a
│   ├── vscode.md                    §2b
│   └── rider.md                     §2c
├── upgrading.md                     §3
├── feature-overview.md              §4
├── editing-features/
│   ├── index.md
│   ├── syntax-highlighting.md       §5a (F1)
│   ├── diagnostics.md               §5b (F3/F4)
│   ├── completion.md                §5c (F7/F8)
│   ├── formatting.md                §5d (F11/F12)
│   ├── comment-uncomment.md         §5e (F13)
│   ├── code-folding.md              §5f (F10)
│   ├── document-outline.md          §5g (F9)
│   ├── code-lens.md                 §5h (F18 + F24 + F25 — revised)
│   └── inlay-hints.md               §5i (F23)
├── navigation-features/
│   ├── index.md
│   ├── go-to-definition.md          §6a (F5)
│   ├── find-usages.md               §6b (F14)
│   ├── find-unused.md               §6c (F15)
│   ├── hook-navigation.md           §6d (F17)
│   └── rename-step.md               §6e (F16)
├── defining-steps.md                §7 (F6, F19)
├── settings.md                      §8
├── editorconfig.md                  §9
└── troubleshooting.md               §10
```

## Status of the drafting pass

Every page above has real prose drafted from
`docs/LSP-IDE-Support-Feature-Designs.md`'s end-user-experience sections
and IDE support matrices, plus `TODO(media)` markers at each point a
screenshot or gif still needs to be captured against a live IDE session
(see the media-need summary in the original issue #63 comment). Nothing
below is placeholder-only, but nothing has real screenshots yet either —
capturing those against live VS/VS Code/Rider sessions is the next pass.

Two items called out in the original outline as needing live verification
before being documented as supported are carried forward as open
`{admonition}` notes on their respective pages rather than asserted as
fact:
- Rider step completion (`editing-features/completion.md`) — no
  Rider-specific completion source found; may auto-negotiate via the
  platform's generic LSP support, unconfirmed.
- Rider step-definition scaffolding (`defining-steps.md`) — same caveat.
