# Contributing to Reqnroll.IdeSupport

Thanks for your interest in contributing. This repo hosts a shared LSP server plus per-IDE
clients — most contributor workflows are area-specific, so start with the guide for the part
you're touching:

| Area | Guide |
|---|---|
| LSP server (`src/LSP/`) — parsing, discovery, diagnostics, all protocol handlers | [src/LSP/CONTRIBUTING.md](src/LSP/CONTRIBUTING.md) |
| Visual Studio extension (`src/VisualStudio/`) | [src/VisualStudio/CONTRIBUTING.md](src/VisualStudio/CONTRIBUTING.md) |
| VS Code extension (`src/VSCode/`) | [src/VSCode/CONTRIBUTING.md](src/VSCode/CONTRIBUTING.md) |

If your change touches more than one area (a new feature usually touches the server plus at least
one client), read the guides for each area you touch — build/test/debug workflows differ
significantly between the .NET server/VS pieces and the TypeScript VS Code piece.

## Before you start

- Skim [docs/LSP-IDE-Support-Overview.md](docs/LSP-IDE-Support-Overview.md) for scope and
  non-goals, and [docs/LSP-IDE-Support-Open-Questions.md](docs/LSP-IDE-Support-Open-Questions.md)
  for known-open questions and risks — a good fraction of "is X supposed to work like this?"
  questions are already answered (or explicitly marked unresolved) there.
- Check the [Issues](../../issues) tab for existing tracked defects/to-dos before starting
  something that might already be in flight.
- For anything non-trivial, a short design note (even a few sentences in the issue) before writing
  code saves rework — this project has a habit of discovering that the "obvious" approach doesn't
  actually work the way it looks (see the eager-startup as-built note in the Architecture doc for
  an example of two rounds of exactly that).

## General workflow

- Branch from `main`; there is no long-lived `develop` branch.
- Keep commits scoped and use a `type(scope): summary` style commit message where it fits
  naturally (`feat(vs): …`, `fix(lsp): …`, `chore(vscode): …`) — not enforced, but it's the
  prevailing style in the history and makes `git log` skimmable.
- Add or update tests for the area you're changing (each area guide explains what's realistically
  testable and what isn't — VS-COM glue and similar host-only integration points generally aren't).
- Update the relevant design doc if your change alters as-built behavior described there — stale
  docs are worse than no docs. `docs/AsBuilt-Reconciliation-Reminder.md` is a live checklist for
  folding a shipped feature's as-built details back into the canonical design docs; use it as a
  template if you introduce a similar standalone implementation plan.
- Once an implementation plan doc in `docs/` is fully shipped, move it to `docs/Archive/` (see the
  existing entries there for the pattern) rather than leaving a "done" plan sitting alongside the
  active ones.

## Reporting bugs / requesting features

Use the [Issues](../../issues) tab. Include, where relevant: which IDE (VS/VS Code), repro steps
against a minimal `.feature`/`.cs` pair if possible, and the runtime logs described in the
area-specific CONTRIBUTING guide (server-debug + client-inspector logs together are usually what's
needed to diagnose an LSP-protocol-level issue).

To get more verbose logs out of the server itself when reproducing a bug, see
[src/LSP/CONTRIBUTING.md](src/LSP/CONTRIBUTING.md#server-logging-and-trace-verbosity) for the
`--log-level`/`--protocol-log-level`/`--trace` command-line flags, and the VS/VS Code guides for
how to raise them from each client.
