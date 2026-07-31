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
