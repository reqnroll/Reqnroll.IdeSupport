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
full media-need summary. Drop captured assets under `_static/` and reference
them with standard Markdown image syntax; update the `TODO(media)` note to a
real `![...](...)` embed once captured.
