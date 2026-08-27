# [vNext]

## Improvements:

* Run CodeLens now resolves each scenario's test target on demand instead of walking the whole `.feature` file on every refresh, fixing it getting stuck on very large feature files (VS, VS Code, Rider) - see #495
* Run CodeLens's Details popup now has a "Show in Test Explorer" action alongside Run/Debug, jumping straight to the test's native pass/fail state and run history (VS) - see #504
* Run CodeLens now shows the scenario's last-run pass/fail glyph, matching VS's own test CodeLens (VS) - see #504
* Run, hook-match-count, and step-hooks CodeLenses on a `Scenario:` line now appear in a deterministic order (Run, then hook count, then step-hooks) instead of an unspecified tie (VS) - see #504
* `.feature` scenarios now have a native Testing-sidebar presence (run/rerun, pass/fail history, and a failed-step marker via the standard Testing API) instead of a CodeLens-only "▶ Run" action with hand-rolled decorations (VS Code) - see #504
* The Test Results "Output" tab now shows Reqnroll's captured step trace for a run, instead of "The test run did not record any output" (VS Code) - see #504

## Bug fixes:

* Fixed the Run action failing with a generic error when `dotnet` isn't on the IDE process's `PATH` (e.g. macOS GUI-launched apps) - now falls back to `DOTNET_ROOT` and well-known install locations, and shows a specific message when `dotnet` still can't be found (VS Code, Rider) - see #452
* Fixed the step-usage "N step usages" CodeLens rendering below the binding method's declaration instead of above it, for connector-discovered bindings (LSP server) - see #484

*Contributors of this release (in alphabetical order):*

* [@clrudolphi](https://github.com/clrudolphi)

---

Development prior to this changelog is not recorded here — see the
[commit log](https://github.com/reqnroll/Reqnroll.IdeSupport/commits/master) for that history.
