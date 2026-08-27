# [vNext]

## Improvements:

* Run CodeLens now resolves each scenario's test target on demand instead of walking the whole `.feature` file on every refresh, fixing it getting stuck on very large feature files (VS, VS Code, Rider) - see #495

## Bug fixes:

* Fixed the step-usage "N step usages" CodeLens rendering below the binding method's declaration instead of above it, for connector-discovered bindings (LSP server) - see #484

*Contributors of this release (in alphabetical order):*

* [@clrudolphi](https://github.com/clrudolphi)

---

Development prior to this changelog is not recorded here — see the
[commit log](https://github.com/reqnroll/Reqnroll.IdeSupport/commits/master) for that history.
