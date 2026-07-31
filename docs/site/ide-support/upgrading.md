# Upgrading

Each IDE shows a first-run "what's new" experience the first time you open
it after a fresh install or an upgrade to a new version — the extension
distinguishes "first install" from "upgrade" so you're not shown the same
welcome content on every update.

* **Visual Studio** — the existing Reqnroll for Visual Studio extension's
  welcome page (ported WPF UI) and version-detection logic are reused, so an
  upgrade is detected the same way it already is for that extension.
* **VS Code** — a native [Walkthrough](https://code.visualstudio.com/api/ux-guidelines/walkthroughs)
  is shown.
* **Rider** — the plugin's own change-notes panel is shown.

TODO(media): 🎬 gif of the Visual Studio "What's New" panel appearing after
an upgrade — short, since it's a one-time UI moment users won't otherwise see.

## Release notes

Where to find release notes / a changelog differs per IDE — link out to
each IDE's native mechanism (VS: the "What's New" panel above; VS Code: the
Walkthrough / Marketplace changelog tab; Rider: the plugin change-notes
panel / JetBrains Marketplace page).
