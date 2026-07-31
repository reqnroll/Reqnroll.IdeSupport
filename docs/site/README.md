# docs/site — user documentation for docs.reqnroll.net

This folder authors the user-facing documentation for the Reqnroll IDE
Support (Preview) extensions, next to the code that implements the features
it describes. See issue [#63](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/63)
for the content outline and the reasoning behind maintaining docs here
instead of directly in `reqnroll/Reqnroll`.

## Layout

Only `ide-support/` is meant to ship to docs.reqnroll.net. Everything else in
this folder (`conf.py`, `Makefile`, `.devcontainer/`, this `index.md`) exists
solely so the tree builds and previews standalone with Sphinx from this repo,
without checking out `reqnroll/Reqnroll`.

```
docs/site/
├── .devcontainer/        local dev container for building/previewing docs
├── conf.py, Makefile,    local-preview-only Sphinx scaffold (not synced)
│   make.cmd, index.md
└── ide-support/          <-- the actual content that ships; sync target is
                              docs/ide-integrations/ide-support/ in
                              reqnroll/Reqnroll
```

`ide-support/` mirrors the MyST/`{toctree}` conventions already used by
`docs/ide-integrations/{visual-studio,rider,vscode}/` in `reqnroll/Reqnroll`,
so a sync can drop the folder in with no reformatting.

## Building locally

Open this repo in the dev container defined in `.devcontainer/devcontainer.json`
(VS Code: "Reopen in Container"), or install the requirements yourself:

```bash
pip install -r docs/site/requirements.txt
```

Then, from `docs/site/`:

```bash
sphinx-autobuild . _build/html
```

This serves a live-reloading preview (default `http://127.0.0.1:8000`).
A one-shot build is `make html` (or `make.cmd html` on Windows without `make`).

## Publishing to docs.reqnroll.net

Not yet automated. Per the plan in issue #63, the intent is a GitHub Actions
workflow, triggered on push to `main` and path-filtered to `docs/site/**`,
that copies `ide-support/` into `reqnroll/Reqnroll`'s
`docs/ide-integrations/ide-support/` and opens a PR there via a cross-repo
token — reviewed and merged like any other change to that repo, which
triggers its existing `build-docs` CI/ReadTheDocs deploy unmodified. Until
that workflow exists, publishing is a manual copy.

## Media (screenshots / gifs)

Per-page `TODO(media)` notes mark where a screenshot or gif still needs to be
captured against a real IDE session — see the outline in issue #63 for the
full media-need summary. Every `TODO(media)` note states the exact target
path for its file — captured media has a designated destination before it
exists, not a "figure it out later" dumping ground.

### Where files go

Every page's media lives in a **sibling folder with the same base name as
the page**, right next to it — never in a shared/centralized asset folder:

```
ide-support/editing-features/syntax-highlighting.md
ide-support/editing-features/syntax-highlighting/    ← its media, here

ide-support/installation/index.md
ide-support/installation/index/                      ← its media, here

ide-support/upgrading.md
ide-support/upgrading/                               ← its media, here
```

This is deliberate: the reference from inside a page to its own media is
**always exactly one path segment down** (`<page-name>/<file>`), regardless
of how deep the page sits in the tree. There's no path-depth arithmetic to
get wrong when writing the embed, and no shared dumping ground where
unrelated pages' assets pile up together — the failure mode that made the
legacy docs painful to maintain.

The empty destination folders (each holding a `.gitkeep`) already exist for
every page that currently has a `TODO(media)` note — find the right one by
matching the page's own name, not by guessing. If you add a brand-new page
with media later, create its sibling folder the same way.

### File naming

`<ide>[-<variant>].<ext>` — for example:

```
editing-features/syntax-highlighting/vs.png
editing-features/syntax-highlighting/vscode.png
editing-features/syntax-highlighting/rider.png

editing-features/diagnostics/vs-squiggle.png
editing-features/diagnostics/vs-fix.gif
```

- **`<ide>`** is always one of `vs`, `vscode`, `rider` — the same three
  codes used everywhere else on this site (`:sync:` keys, the support-matrix
  columns). One consistent vocabulary for "which IDE" across the whole
  project, not a new naming scheme per asset type.
- **`<ext>`** is `.png` for a static screenshot, `.gif` for an animated
  capture. Extension alone tells you which kind it is — no redundant
  `-screenshot`/`-gif` suffix needed.
- **`<variant>`**, only when a page needs more than one asset for the same
  IDE (e.g. a required screenshot plus an optional gif, or several distinct
  UI states) — a short kebab-case qualifier, e.g. `-squiggle`, `-fix`,
  `-hook-match`. Omit it entirely when there's exactly one file per IDE.

This scheme is deliberately boring: given a page and an IDE, the file's
path and name are fully determined, never a judgment call.

### Capture conventions

Beyond file placement/naming, follow these so every capture looks like it
belongs to the same documentation set rather than a patchwork of whoever
grabbed it and however their machine happened to be set up that day:

**Environment**

- **Windows for all three IDEs.** Rider and VS Code are cross-platform;
  mixing a macOS-titlebar Rider capture next to a Windows VS Code capture
  in the same tab trio looks inconsistent when a reader flips between
  them. Visual Studio is Windows-only anyway, so this keeps all three
  uniform.
- **A named light theme per IDE, not "whatever the default happens to
  be."** IDE defaults don't agree with each other — VS Code actually
  ships dark by default (Dark Modern), Visual Studio's is a mid-tone blue,
  Rider's genuinely is light. Rather than "default," use a specific light
  theme per IDE so results are reproducible and consistent across
  captures: **VS Code — "Light+ (default light)"**; **Visual Studio —
  "Blue" theme (or "Light")**; **Rider — "Light"** (this one already is
  Rider's real default). Light was chosen because squiggles, CodeLens
  text, and inlay hints all read more clearly at small embed sizes
  against a light background than a dark one.
- **Default window/menu layout** — no custom panel arrangements, no
  personal toolbar customizations. A reader with a stock install should
  recognize the layout.
- **Fixed OS display scaling (100%) and the IDE's default editor font
  size**, unless the feature being documented specifically concerns font
  size or DPI. Keeps proportions consistent across captures taken at
  different times.
- **The shared [Reqnroll Quickstart sample project](https://github.com/reqnroll/Quickstart)**
  as the fixture for every capture, rather than an ad hoc feature file
  per screenshot — a reader who worked through the Quickstart guide
  recognizes the same scenarios throughout this site, and it keeps
  file/project names in title bars and breadcrumbs generic (no real
  usernames or local machine paths leaking into a screenshot).

**Framing**

- **Crop to the relevant editor pane plus just enough chrome for
  orientation — not a literal whole-window screenshot, and not an
  extreme close-up either.** A full IDE window shrunk to a ~700px-wide
  doc column makes small things (a squiggle, one CodeLens annotation, a
  single inlay hint) illegible; that defeats the point of having the
  screenshot at all. Reserve genuine full-window captures for
  install/settings pages where the surrounding chrome (the Options
  dialog, the Marketplace panel, the status bar widget) *is* the subject.
- **Split editor view when a capture needs to show cause-and-effect
  across a `.feature` file and its `.cs` binding** — e.g. a CodeLens
  count updating after a `.cs` edit, or Rename updating both sides at
  once. Use each IDE's native split (VS: New Vertical Tab Group; VS Code:
  Split Editor Right; Rider: Split Vertically). Let the split be uneven
  rather than a strict 50/50 if one side needs more width to stay
  readable.
- **Turn off every toggleable overlay except on the page demonstrating
  it** — inlay hints and CodeLens both add extra inline content that
  shifts line layout and clutters an unrelated screenshot. Enable only
  the one this specific page is about.
- **A consistent maximum embed width** across the site (resize down to
  it rather than embedding at native resolution) so images don't vary
  wildly in size/proportion from page to page.

**Gifs specifically**

- **Enlarge the mouse pointer**, and turn on click-highlighting (a
  burst/ripple on click) if your capture tool supports it (ScreenToGif,
  Kap, LICEcap all do) — many of these captures are "right-click → menu
  item," and without a click indicator it's hard to tell exactly where
  and when the click landed.
- **Pause briefly on the payoff frame** (the popup fully open, the jump
  landed, the count updated) before the loop restarts, so a reader who
  glances over mid-loop still catches the point.
- **Keep them short and compressed** (gifsicle or ffmpeg) before
  committing — a few seconds of relevant motion, not a leisurely
  narrated walkthrough, and small enough that the docs repo doesn't
  bloat.

### Fulfilling a `TODO(media)` note

1. Capture the screenshot/gif against a live IDE session.
2. Save it at the exact path the note's `**Target:**` line states — that
   path is already correct relative to the `.md` file it's in, no
   adjustment needed.
3. Replace the `TODO(media)`/`**Target:**` lines with a standard Markdown
   image embed using that same path, e.g. from `syntax-highlighting.md`:
   `![Syntax highlighting in Visual Studio](syntax-highlighting/vs.png)`.
4. If a `TODO(media)` note doesn't yet state a `**Target:**` (shouldn't
   happen going forward, but flag it if you find one), pick
   `<page-name>/<ide>[-<variant>].<ext>` following the convention above
   rather than dropping the file wherever's convenient.

### Tab-set pattern for per-IDE media

Wherever a feature's screenshot/gif differs by IDE, the media lives in a
**synced tab set** (via the `sphinx_design` extension) so a reader picks
their IDE once and every subsequent page remembers it — see
`ide-support/editing-features/syntax-highlighting.md` for the reference
example. The pattern:

`````markdown
:::{tab-set}

````{tab-item} Visual Studio
:sync: vs

![Syntax highlighting in Visual Studio](syntax-highlighting/vs.png)
````

````{tab-item} VS Code
:sync: vscode

![Syntax highlighting in VS Code](syntax-highlighting/vscode.png)
````

````{tab-item} Rider
:sync: rider

![Syntax highlighting in Rider](syntax-highlighting/rider.png)
````

:::
`````

Always use exactly these three `:sync:` keys (`vs`, `vscode`, `rider`) so
every tab-set on the site shares one selection — no `:sync-group:` override
needed, they default to the same group. The selection is remembered via
`sessionStorage` (per browser tab, cleared when the tab closes — not a
permanent per-visitor preference), which is enough to carry a choice across
every page in one reading session.

Since `reqnroll/Reqnroll`'s own `conf.py` doesn't currently load
`sphinx_design`, add it there as part of whatever change first syncs a
tab-set into that repo, or the build will raise an unknown-directive error.
