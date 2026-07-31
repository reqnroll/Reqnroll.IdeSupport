# Configuration file for the Sphinx documentation builder.
#
# This is a LOCAL PREVIEW configuration only. The content under ide-support/
# is written to be synced into reqnroll/Reqnroll's docs/ide-integrations/
# tree (which has its own conf.py, theme, and Google Analytics wiring) — this
# file exists so contributors can build and preview the pages from this repo
# without checking out reqnroll/Reqnroll. Keep MyST settings here in sync
# with reqnroll/Reqnroll's docs/conf.py so preview rendering matches production.
#
# NOTE: 'sphinx_design' below is not currently in reqnroll/Reqnroll's own
# conf.py/requirements.txt. It's needed for the per-IDE tabbed screenshots/
# gifs used throughout ide-support/ (see tab-set usage, e.g.
# ide-support/editing-features/syntax-highlighting.md) — add it there too
# as part of the sync, or the tabs will fail to render / raise an unknown
# directive error on that build.

project = 'Reqnroll IDE Support'
copyright = '2024-2026, Reqnroll'
author = 'Reqnroll'

extensions = [
    'myst_parser',
    'sphinx_copybutton',
    'sphinx_design',
]

templates_path = ['_templates']
exclude_patterns = ['_build', 'Thumbs.db', '.DS_Store']

master_doc = 'index'

# -- Options MyST -------------------------------------------------

myst_enable_extensions = [
    "attrs_block",
    "colon_fence",
    "attrs_inline",
]

myst_heading_anchors = 3

# -- Options for HTML output -------------------------------------------------

html_theme = "furo"
html_static_path = ['_static']
html_title = '%s Documentation (Preview)' % project
