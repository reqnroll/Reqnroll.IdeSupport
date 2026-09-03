# Troubleshooting / FAQ

## Can I have both extensions installed at once?

Both **can** be installed side by side — installing one doesn't remove the
other. But running both **enabled** at the same time is **not a supported
configuration**: with both active, you'll get duplicate/conflicting
behavior (e.g. two sets of diagnostics, two CodeLens annotations) for the
same `.feature` files.

If you have both installed, disable one: **Extensions → Manage
Extensions**, select the extension you're not using, and click
**Disable**. See [Installation](installation/index.md) (Visual Studio tab)
for how to tell the two listings apart in the Marketplace.

## Known per-IDE limitations

* **Visual Studio** — the native Document Outline window does not show
  `.feature` file structure. See [Document Outline](editing-features/document-outline.md).
* **Visual Studio** — native "Find All References" (Shift+F12) does not
  route to Reqnroll step bindings; use the dedicated entry point instead.
  See [Find Step Definition Usages](navigation-features/find-usages.md).
* **VS Code** — Rename doesn't yet support disambiguating a step bound to
  more than one candidate binding. See [Rename Step](editing-features/rename-step.md)
  for the workaround (Rider and Visual Studio both handle this case).

## A shared `.feature` file shows hooks, diagnostics, or highlighting from the "wrong" project

If the same `.feature` file is linked into more than one project — for example, a
`Calculator.feature` that physically lives in `ProjectA` and is also linked into `ProjectB` —
Code Lens hook-match counts, **Go to Hooks**, diagnostics/squiggles, and syntax highlighting for
that file always reflect **the project that physically contains the file on disk**, regardless
of which project's node you used to open it (Solution Explorer, VS Code's Explorer, or Rider's
Project view).

This is deterministic, and by design rather than a bug: opening a file only identifies it by its
path, with no way to know which project's node you clicked through to get there, so a single
project has to be picked to drive what's shown. It's always the file's **home project** — the
one whose folder physically contains it — never whichever project you happened to navigate from.

If bindings differ between the two projects, expect Code Lens, [Hook Navigation](navigation-features/hook-navigation.md),
and diagnostics on the shared file to reflect the home project's bindings only, even when the
file is viewed "from" the other project.

## Visual Studio: GitHub Copilot suggestions compete with `.feature` file editing

Visual Studio does not offer a per-file-type or per-content-type way to turn off GitHub Copilot
(inline "ghost text" suggestions, or the lightbulb's Copilot-provided "Fix" action) for `.feature`
files specifically. Once Copilot is enabled in the
IDE at all, it applies uniformly to every open document — there's no content-type or file-extension
scoping to opt out of, and no supported extensibility point Reqnroll IDE Support could hook to
suppress it automatically for Gherkin. The empty "Fix" lightbulb that spins forever on a
`reqnroll.parser`/`reqnroll.binding` diagnostic is this same Copilot quick-fix provider finding
nothing to offer — not a Reqnroll IDE Support action.
IntelliCode's separate whole-line completions don't apply here — that feature is C#-only and never
activates on `.feature` files.

If Copilot's suggestions are getting in the way while editing Gherkin, the available controls are
all IDE-wide (Visual Studio has no per-language settings page for the `.feature` content type to
scope any of these narrower):

* **Turn off Copilot completions entirely** — click the **Copilot** badge (top-right of the editor)
  → uncheck **Completions**, or **Tools → Options → GitHub → Copilot → Completions**.
* **Make suggestions manual instead of automatic** (a lighter touch — keeps Copilot available on
  demand everywhere, including `.feature` files) — **Tools → Options → Text Editor → Inline
  Suggestions → General**, set **Inline Suggestions Invocation** to **Manual**. Trigger a suggestion
  only when wanted with **Alt+.** / **Alt+,**.
* **Org-managed exclusion by path** — if your organization has GitHub Copilot Business or
  Enterprise, an admin can configure
  [Content Exclusion](https://learn.microsoft.com/visualstudio/ide/visual-studio-github-copilot-admin#configure-content-exclusion)
  for a path pattern like `**/*.feature`, which blocks both completions and Chat context for
  matching files repo- or org-wide. This is configured server-side by an admin, not from within
  Visual Studio, and isn't available on individual/free Copilot plans.

## Where are the logs, and how do I change the log level?

:::{tab-set}

```{tab-item} Visual Studio
:sync: vs

Log files are written to `%LOCALAPPDATA%\Reqnroll\`, one set per process,
named `reqnroll-vs-<role>-<yyyyMMdd>-<pid>.log`:

- `reqnroll-vs-server-*.log` — the LSP server's application log
- `reqnroll-vs-protocol-*.log` — protocol/wire-level internals
- `reqnroll-vs-ext-*.log` — the Visual Studio extension side

There's no Output window pane for these — check the files directly.

**Changing the log level:** there's no in-product setting. A normal
(released, VSIX-installed) build runs at `Warning` level by default. The
`REQNROLLVS_DEBUG` environment variable (set it to `1`, `true`, or a
[`TraceLevel`](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.tracelevel)
name, e.g. `Verbose`) raises the verbosity of the **extension-side**
(`reqnroll-vs-ext-*.log`) logger, but does not affect the LSP server's own
`reqnroll-vs-server-*.log`/`reqnroll-vs-protocol-*.log` verbosity — see the
note below.
```

```{tab-item} VS Code
:sync: vscode

Two Output channels (**View → Output**, then pick from the dropdown):

- **Reqnroll LSP** — the standard client/server log
- **Reqnroll LSP Trace** — LSP wire trace (only populated when tracing is
  enabled, see below)

**Changing the log level:** set `"reqnroll.trace.server"` in
`settings.json` to `"off"`, `"messages"`, or `"verbose"`. Setting it to
`"verbose"` also writes a timestamped trace file under
`%LOCALAPPDATA%\Reqnroll\` (Windows) or `~/Library/Logs/Reqnroll/` (macOS):
`reqnroll-vscode-inspector-<timestamp>.log`. **Reload the window** after
changing this setting for it to take effect.
```

```{tab-item} Rider
:sync: rider

Log files are written to a per-OS Reqnroll log directory — Windows
`%LOCALAPPDATA%\Reqnroll\`, macOS `~/Library/Logs/Reqnroll/`, Linux
`~/.local/share/Reqnroll/` — named `reqnroll-rider-<role>-<yyyyMMdd>-<pid>.log`
(`ext` for the plugin side, `server`/`protocol` for the LSP server). These
are not written to Rider's own `idea.log` or a dedicated tool window.

**Changing the log level:** there's no in-product setting or documented
environment variable for Rider. The plugin logs at `Verbose` only in a
development sandbox instance; a normal installed build runs at `Warning`.
```

:::

```{admonition} No unified, user-facing log-level setting yet
:class: note

Log-level configuration is inconsistent across the three IDEs today — VS
Code has a real setting, Visual Studio only has a partial (extension-side
only) environment-variable override, and Rider has neither. This gap is
already tracked in [issue #291](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/291),
which covers giving all three IDEs a real, shared way to change the LSP
server's log level. If you need verbose logs for a bug report and your IDE
doesn't currently support raising the level, say so on that issue (or on
your bug report) — it's useful signal for prioritizing it.
```

## How do I report a bug?

File an issue on the
[Reqnroll.IdeSupport repository](https://github.com/reqnroll/Reqnroll.IdeSupport/issues),
including your IDE and version, the extension version, and — if possible —
the relevant log file (see [Where are the logs](#where-are-the-logs-and-how-do-i-change-the-log-level)
above).

## Where does telemetry data go?

TODO: document the extension's telemetry policy here once finalized (what
is/isn't collected, and how to opt out) — cross-reference the relevant
privacy documentation once published.
