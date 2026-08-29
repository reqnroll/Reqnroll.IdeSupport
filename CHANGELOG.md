# [vNext]

## Improvements:

* Run CodeLens now resolves each scenario's test target on demand instead of walking the whole `.feature` file on every refresh, fixing it getting stuck on very large feature files (VS, Rider) - see #495
* Run CodeLens's Details popup now has a "Show in Test Explorer" action alongside Run/Debug, jumping straight to the test's native pass/fail state and run history (VS) - see #504
* Run CodeLens now shows the scenario's last-run pass/fail glyph, matching VS's own test CodeLens (VS) - see #504
* Run, hook-match-count, and step-hooks CodeLenses on a `Scenario:` line now appear in a deterministic order (Run, then hook count, then step-hooks) instead of an unspecified tie (VS) - see #504
* Go to Step Definition's ambiguous-match picker now shows the target method's source line instead of a method name/step-type label, built from the standard `textDocument/definition` response instead of a Reqnroll-specific message (VS Code) - see #126
* Post-build binding rediscovery now relies solely on the server's standard LSP dynamic file-watch registration instead of a redundant client-side watcher, after confirming the canonical path reliably detects real `dotnet build`s on its own (VS Code) - see #31
* `.cs` binding methods that fail Reqnroll's structural validation (non-static where required, async void, a malformed step expression or `[Scope(Tag=...)]` tag expression, etc.) now get a live diagnostic squiggle on the offending attribute, merging alongside the IDE's own C# diagnostics for the same file; `.feature` "step not found" diagnostics also now name the specific reason when the step structurally matches an invalid binding instead of a generic message (LSP server) - see #514

## Bug fixes:

* Fixed the Run action failing with a generic error when `dotnet` isn't on the IDE process's `PATH` (e.g. macOS GUI-launched apps) - now falls back to `DOTNET_ROOT` and well-known install locations, and shows a specific message when `dotnet` still can't be found (Rider) - see #452
* Fixed the step-usage "N step usages" CodeLens rendering below the binding method's declaration instead of above it, for connector-discovered bindings (LSP server) - see #484
* Fixed F2 doing nothing (or erroring "Rename not available at this location") on a `.cs` binding attribute - now runs Reqnroll's Rename Step, falling back to the native C# rename everywhere else in a `.cs` file so it doesn't hijack renaming an ordinary symbol, including the binding method's own name (VS Code) - see #506

*Contributors of this release (in alphabetical order):*

* [@clrudolphi](https://github.com/clrudolphi)

---

Development prior to this changelog is not recorded here — see the
[commit log](https://github.com/reqnroll/Reqnroll.IdeSupport/commits/master) for that history.
