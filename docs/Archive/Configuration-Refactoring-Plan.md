# Configuration Refactoring Plan

Status: proposed (not started), revised after design review on 2026-10-22. Findings come from reading the code
and tests; nothing here has been run. Items still unverified are listed in section 8.

## 1. Background

Issue #1270 reports that `language.feature` in `reqnroll.json` is ignored: a French `.feature` file with no
`#language` directive is parsed as English. Tracing the flow showed the cause is not a file-level English
default overriding the config. The parse path never sees the project's configuration.

### Language flow as designed

1. `ProjectScopeIdeSupportConfigurationProvider` finds `reqnroll.json` (or `specflow.json` / `App.config`,
   plus `specsync.json` and `deveroom.json`) in the project folder and loads it.
2. `ReqnrollConfigDeserializer` maps `language.feature` to `IdeSupportConfiguration.DefaultFeatureLanguage`
   (default `"en-US"`).
3. `ReqnrollGherkinDialectProvider.Get(DefaultFeatureLanguage)` supplies the default dialect to
   `IdeSupportGherkinParser`/`TokenMatcher`.
4. A `#language:` line in the file overrides the default for that file only.

### Where it breaks

- The server registers one global `IIdeSupportConfigurationProvider`:
  `ProjectSystemIdeSupportConfigurationProvider` (`ServiceCollectionExtensions`). It is a stub that returns
  `new IdeSupportConfiguration()` (`//TODO: Load solution-level config`), so it always reports `en-US`.
- `IdeSupportTagParser` (singleton) and `FormattingHandler` take that stub.
- `IIdeSupportTagParser.Parse(snapshot, registry)` has no URI, project or language parameter, so config cannot
  be per project. `ParseInternal` reads only `DefaultFeatureLanguage` from the configuration.
- Diagnostics, semantic tokens, step matching, completion context and the workspace tag index all derive from
  that parse.
- `FormattingHandler` reads the whole configuration from the stub, not only the language. Formatting settings
  from `reqnroll.json`/`deveroom.json` are ignored today for the same reason.
- Completion fallback, document links and code actions already use the per-project provider via
  `ILspWorkspaceScopeManager.GetConfigurationProviderForUri`, which is why they behave correctly for files inside
  a project folder.

### Two ways of mapping a URI to a project

The fix must not introduce a second inconsistency, so this matters:

| Lookup | Used by | Resolution |
|---|---|---|
| `ResolvePrimaryOwner(uri)` | `GetRegistryForUri` (binding registry), match-set owner key | Membership index, then home-project rule. Handles linked files. |
| `ResolveOwners(uri)` | `RescanClosedFileAsync` | Membership index; every owning project. |
| `GetProjectForUri(uri)` | `GetConfigurationProviderForUri`, `WatchedFilesHandler` | Longest containing project folder only; ignores the membership index. |

A feature file linked from outside its project folder resolves its registry from the primary owner but its
configuration from no project (default `en-US`). `ScanClosedFileAsync` parses once per owning project with that
project's own registry, so its configuration must come from the same project, not from the URI.

### Config changes do not refresh everything that depends on the parse

Once the parse depends on configuration, every cache derived from it must be refreshed when configuration
changes:

- `ReqnrollConfigChangedHandler` reparses open buffers under the changed project's folder (folder prefix), so open
  linked files outside that folder are missed.
- Closed-file match sets are rebuilt by `BindingRegistryChangedHandler` when the registry changes. A
  language-only change may leave the registry unchanged, leaving step-usage counts and Find Unused stale
  (unverified; see section 8).
- `FeatureTagIndex` caches per-file tag counts keyed on last-write time, so a config change never invalidates it.
- `WatchedFilesHandler` watches only `**/reqnroll.json` (and `.editorconfig`). Edits to `specflow.json`,
  `App.config`, `deveroom.json` and `specsync.json` reload nothing, although the provider loads all of them.

### Why tests did not catch it

- Config loading is tested (`ProjectScopeIdeSupportConfigurationProviderTests`, `ReqnrollConfigDeserializerTests`).
- The tag parser is tested, but every test constructs `IdeSupportTagParser` with a mocked or hand-made provider.
  Production wires a different one. Nothing asserts the production wiring.
- The stub has a test (`ProjectSystemIdeSupportConfigurationProviderTests`) that pins its limitation as "known".
- The Specs project has language scenarios only for the file-level `# language: de` directive, which bypasses
  config. There is no spec for a project-level language and no multi-project language case.

## 2. Goals

- Fix #1270 so the project's configured language drives parsing, formatting, completion and the tag index,
  for open and closed files, including linked files.
- Make it impossible for a server component to obtain configuration without naming a project.
- Remove configuration code inherited from the port that nothing uses.
- Give `Configuration/` a structure with clear cohesion, if the cost of the move is justified (Phase 5).

## 3. Non-goals

- Introducing real solution-level settings. Nothing reads them today; revisit only if a requirement appears.
- Changing the configuration file formats or the precedence between them.
- Behavioural changes beyond those listed in Phase 1. Phase 1 deliberately changes one more behaviour than the
  language: project formatting settings start to apply (see Phase 1, "Formatting").
- The out-of-process connector's own `LegacyAppConfigLoader`. It runs in a different process with different
  inputs; Phase 4 does not merge it.

## 4. Phases

Branch from `main`; do not commit to `main`. Phase 0 tests 1-6 ship in PR A1 together with the fix (tests in the
first commit, fix in later commits) because they fail on `main` and a tests-only PR would leave CI red. Each
later phase is its own PR. Keeping the bug fix (A1), the staleness work (A2) and the formatting-settings change
(A3) apart keeps each reviewable and independently revertable.

### Phase 0: Failing tests first (first commit of PR A1; test 7 belongs to A2)

Add tests that fail on current `main`. Prefer positive assertions: "no diagnostics" can pass simply because
diagnostics have not been published yet.

1. Server-level test using the real DI container: project with `reqnroll.json` `language.feature = "fr"`,
   `didOpen` of a French file with no `#language` line. Assert that a French step is parsed as a step and
   matches its binding, and that there are no parser-error diagnostics.
2. Spec scenario in `Reqnroll.IdeSupport.LSP.Server.Specs`: project-level language, no directive in file.
   Needs a step to place a `reqnroll.json` in the test project.
3. Two projects with different languages, each file parsed with its own project's language.
4. A feature file linked into a project from outside its project folder, parsed with the owning project's
   language (open and closed).
5. A feature file shared by two projects with different languages: the closed-file match set for each owner is
   built with that owner's language.
6. `FormattingHandler` formats a French file under the project config.
7. Changing only `language.feature` in `reqnroll.json` reparses open files, refreshes closed-file match sets,
   and refreshes the tag index.

### Phase 1: Fix #1270 (PR A1)

**Parser contract.**

- Change `IIdeSupportTagParser.Parse` to take the default feature language explicitly:
  `Parse(snapshot, registry, defaultFeatureLanguage)`. The parser only reads that one value, so pass that value
  rather than the whole `IdeSupportConfiguration`. This keeps `LSP.Core` free of workspace types and makes the
  dependency visible.
- Make the parameter required. Do not keep a two-argument overload; it would recreate the trap.
- Remove `IIdeSupportConfigurationProvider` from the `IdeSupportTagParser` constructor.

**Owner-consistent resolution.** Configuration always comes from the same project object as the registry.

- `GherkinDocumentTaggerService.ParseAsync`: resolve the primary owner once, then take both the registry and the
  configuration from it. With no owner, use the registry and configuration fallbacks as today.
- `GherkinDocumentTaggerService.ScanClosedFileAsync`: use `project.GetIdeSupportConfigurationProvider()` for the
  `project` argument, not a URI lookup.
- `GetConfigurationProviderForUri`: switch from `GetProjectForUri` to `ResolvePrimaryOwner`, so completion,
  document links and code actions agree with the parse for linked files.
- `WatchedFilesHandler` and `RenameHandler` keep `GetProjectForUri`. A configuration file belongs to the project
  folder that contains it, so the reload target is the folder project, while readers use the primary owner. Add
  a test where a linked file's primary owner differs from the folder project: after editing the folder
  project's `reqnroll.json`, the linked file is reparsed with the new language and the owner's provider is the
  one that was reloaded. State the chosen split in the PR description.
- `CompletionContextResolver.Resolve`: pass its existing `fallbackLanguage` argument to `Parse`, so the parse and
  the fallback dialect cannot disagree.
- `FeatureTagIndex.CollectCounts`: resolve the language from the file path's primary owner.
- Benchmarks (`tests/Performance/.../CorpusFingerprint.cs`): pass the corpus language.

**Formatting (language only).** `FormattingHandler` resolves the owner's configuration per document URI instead
of injecting a provider, and uses only its `DefaultFeatureLanguage` for parsing. The other format settings keep
their present source until PR A3. Keep the existing `DefaultFeatureLanguage ?? "en"` guard consistent with the
parser's fallback. If the language and the format settings turn out to be inseparable in the handler, fold A3
into A1, add the test and the release note there, and update this plan.

**Close the trap.** Delete the `IIdeSupportConfigurationProvider` DI registration. After the two constructor
changes nothing should inject it. Any future consumer that does fails at container build or first resolution
instead of silently receiving `en-US`. This replaces the earlier idea of a wiring test.

Config changes while files are open are handled in PR A2 below. A1 does not regress them: the existing
`ReqnrollConfigChangedHandler` already reparses open buffers under the changed project's folder.

**Tests.** Signature changes touch the parser construction sites (17 in 16 files), the NSubstitute
`Parse(...)` setups and `Received`/`DidNotReceive` checks in `GherkinDocumentTaggerServiceTests`,
`GherkinDocumentTaggerServiceCloseRaceTests` and `CompletionContextResolverTests` (29), and the benchmarks. Add a shared test helper that creates a parser
so later signature changes stay one-line diffs. Delete `ProjectSystemIdeSupportConfigurationProviderTests` cases
that only pin the stub's DI role; keep any that still describe the fallback until Phase 3.

Keep the no-owning-project fallback (`en-US`) unchanged in this phase.

### Phase 1b: Refresh derived state on config change (PR A2)

Depends on A1. Phase 0 test 7 runs first and decides which items below are needed.

- `ReqnrollConfigChangedHandler`: select open buffers whose primary owner is the changed project, not buffers
  under its folder.
- Refresh closed-file match sets for the changed project's feature files, unless test 7 shows discovery already
  does this when the registry is unchanged.
- `FeatureTagIndex`: include the language in the cache entry, or drop the project's entries on config change.
- Watching the other config file names is deferred to Phase 4. Until then, list it as a known limitation in the
  PR description.

### Phase 1c: Apply project formatting settings (PR A3)

Depends on A1. `FormattingHandler` reads the project's `GherkinFormatConfiguration` instead of the default the
stub supplied. This is an intended behaviour change: users with stale format settings in `reqnroll.json` or
`deveroom.json` may see formatting change. Cover it with a test and mention it in the release notes. Keeping it
separate means reverting it does not revert the language fix.

### Phase 2: Delete dead code (PR B)

Remove, with no behaviour change:

- `Configuration/SpecFlowConfiguration.cs` (entirely commented out).
- Commented-out members and wiring in `ReqnrollConfiguration`, `IdeSupportConfigurationLoader`,
  `ProjectScopeIdeSupportConfigurationProvider` (weak-event lines, `ProjectSystemOnProjectsBuilt`, `Dispose`
  body) and `ProjectSettingsProvider`, including `ProjectSettingsProvider.OnConfigurationChanged`, which exists
  only for the commented-out subscription.
- Other members with no callers, listed explicitly in the PR description so the scope stays reviewable.

Verify each removal with a build and the existing test suites.

### Phase 3: Collapse the provider layer (PR C)

- Delete `ProjectSystemIdeSupportConfigurationProvider` (its DI registration is already gone after Phase 1).
- Replace the "no owning project" fallback in `GetConfigurationProviderForUri` with an explicitly named static
  default configuration. This also stops allocating a new provider per call.
- Delete the unused `IIdeScope` overloads in `ConfigurationProjectSystemExtensions`. Pre-check done: Visual
  Studio reaches configuration only through `VsProjectScope`, which uses the `IProjectScope` overload.
- `IIdeSupportConfigurationProvider.ConfigurationChanged` is raised by `Reload()` on the live
  `WatchedFilesHandler` path but has no subscribers. Replace it with a return value: `Reload()` returns whether
  the effective configuration changed. `WatchedFilesHandler` then publishes `ReqnrollConfigChangedNotification`
  and triggers discovery only on a real change, instead of on every save.
- **Precondition: change detection must be trustworthy.** `IdeSupportConfiguration.Equals` does not compare
  `SnippetExpressionStyle` (set from `trace.stepDefinitionSkeletonStyle`), so a change to only that setting is
  not seen as a change; gating notification on the result would silently drop that edit. The nested `Equals`
  implementations (`Traceability`, `Editor`, `BindingDiscovery`, `Reqnroll`) may also compare arrays by
  reference, which gives false "changed" results. Before gating on the result: make `Equals`/`GetHashCode`
  cover every setting (or compare serialized forms), and add a test that edits each loaded setting in turn and
  expects "changed", and re-saving identical content and expects "unchanged".
- Update `ProjectSystemIdeSupportConfigurationProviderTests`, `LspWorkspaceScopeManagerTests` and the benchmark
  stub provider accordingly.

### Phase 4: Consolidate loaders (PR D)

Today the knowledge of the configuration file names is spread across:

- `GetConfigSources` (which files exist and their precedence),
- the file-name `if` chain in `LoadConfiguration` (which loader to run),
- the `IdeSupportConfigurationLoader` factory methods (which deserializer to use),
- inline XML (`App.config`) and SpecSync parsing inside `ProjectScopeIdeSupportConfigurationProvider`,
- the watcher registration in `WatchedFilesHandler` (only `reqnroll.json`),
- `ReqnrollProjectSettingsProvider`, which references the `App.config` file name.

Changes:

- One component per source format that fills `IdeSupportConfiguration`: `reqnroll.json`/`specflow.json`,
  `deveroom.json`, `App.config` (XML), `specsync.json`.
- One table of config sources (file name, precedence, loader) drives `GetConfigSources`, loader selection and the
  `workspace/didChangeWatchedFiles` registration, so every loaded file is also watched.
- The provider keeps only: locate sources, cache, reload, change detection.
- Behaviour change, not pure consolidation: edits to `specflow.json`, `App.config`, `deveroom.json` and
  `specsync.json` will start triggering reloads and discovery. Call this out in the PR description and test it.

### Phase 5: Restructure `Configuration/` (optional, PR E)

A mechanical move with no behaviour change. It has the lowest value and the most churn (namespace edits in
about 20 consumer files, blame history, conflicts with open branches). Do it only after resolving the open
questions below, and only if they still point to a split.

| Folder | Contents |
|---|---|
| `Configuration/Model/` | `IdeSupportConfiguration`, `ReqnrollConfiguration`, `TraceabilityConfiguration`, `TagLinkConfiguration`, `BindingDiscoveryConfiguration`, `CSharpCodeGenerationConfiguration`, `GherkinFormatConfiguration`, `EditorConfiguration`, `SnippetExpressionStyle`, `ProcessorArchitectureSetting`, `IdeSupportConfigurationException` |
| `Configuration/Loading/` | `IdeSupportConfigurationLoader`, `ReqnrollConfigDeserializer`, `JsonNetConfigDeserializer`, `IConfigDeserializer`, plus the Phase 4 loaders |
| `Configuration/EditorConfig/` | `IEditorConfigOptions`, `IEditorConfigOptionsProvider`, `FileSystemEditorConfigOptions`, `FileSystemEditorConfigOptionsProvider`, `EditorConfigGlob`, `EditorConfigOptionsExtensions`, `EditorConfigSettingAttribute`, `NullEditorConfigOptions`, `NullEditorConfigOptionsProvider` |

- Use sub-namespaces that follow the folders.
- Do the `git mv` in its own commit, separate from the `using` updates, so history follows the files.
- Open question: `EditorConfiguration`/`GherkinFormatConfiguration` are DTOs but are consumed by the
  editorconfig code, and `IdeSupportConfiguration` embeds `EditorConfiguration`. Decide whether they belong in
  `Model/` or beside the editorconfig code before moving.
- Open question: if the editorconfig code is only used by the Gherkin formatter, it could move to `LSP.Core`.
  Check non-LSP consumers (Visual Studio) first.
- Consider renaming `ProjectScopeIdeSupportConfigurationProvider` (and the `ProjectSystem/Configuration`
  folder) once the stub is gone, since the "ProjectSystem" vs "ProjectScope" distinction no longer exists.

## 5. Ordering and dependencies

| PR | Contents | Depends on |
|---|---|---|
| A1 | Phase 0 (tests 1-6) + Phase 1 | — |
| A2 | Phase 0 test 7 + Phase 1b | A1 |
| A3 | Phase 1c | A1; independent of A2 |
| B | Phase 2 | A1 (avoids reworking code Phase 1 touches) |
| C | Phase 3 | A1 (DI registration already removed), A2, B |
| D | Phase 4 | A1; independent of C |
| E | Phase 5 (optional) | All others, so the move carries no logic changes |

References to "PR A" elsewhere in this plan mean A1 and A2 together.

## 6. Risks

- Phase 1 changes a widely used interface; about 46 test sites change. The shared test helper keeps the diff
  mechanical.
- Switching `GetConfigurationProviderForUri` to `ResolvePrimaryOwner` changes which project's configuration
  completion, document links and code actions see for linked files. That is the intent, but it touches features
  that work today for files inside a project folder; Phase 0 tests 4 and 5 cover it.
- Removing the DI registration surfaces any consumer missed by the search as a startup failure. Run the server
  tests and the Specs project, which build the real container.
- Formatting settings from project configuration start to apply (Phase 1c, separate PR A3). Users with stale
  settings in `reqnroll.json`/`deveroom.json` may see formatting change.
- Gating notification on `Reload()` changing the configuration (Phase 3) is only safe once
  `IdeSupportConfiguration.Equals` covers every setting; see the Phase 3 precondition.
- Reader/reloader split: readers use the primary owner while the watcher reloads the folder project; the Phase 1
  linked-file test guards against the two disagreeing.
- Namespace changes in Phase 5 touch many files; do them mechanically and run the full build for all hosts.
- Behaviour on documents outside any project (no owning project) must stay the default `en-US` unless decided
  otherwise.

## 7. Verification

- Targeted `dotnet test` for `Reqnroll.IdeSupport.Common.Tests`, `LSP.Core.Tests` and `LSP.Server.Tests` after
  each phase; the Specs project for PR A and PR C.
- Build the benchmarks project (`tests/Performance`) for PR A and PR C; it calls `Parse` and implements the
  provider interface.
- Live check in Visual Studio with the reproduction from the issue (French file, `reqnroll.json`
  `language.feature = "fr"`, no directive). Also edit `language.feature` while the file is open and confirm it
  reparses. Unit and spec tests alone do not prove the VS path.
- Confirm the full solution builds after PR C and PR E.

## 8. Unverified

- Whether config-triggered discovery republishes an unchanged registry, which would make the closed-file refresh
  in Phase 1 redundant. Phase 0 test 7 answers it.
- Whether Visual Studio reaches configuration anywhere beyond `VsProjectScope`. Only that call site was seen;
  search the whole VS tree before deleting the `IIdeScope` overloads in Phase 3.
- The test-site counts in Phase 1 (17 construction sites in 16 files, 29 `Parse` setups/checks) and the "about 46
  sites" in Risks were counted during the earlier review and not re-counted; recount before estimating the PR.
- Whether the format settings can be separated from the language in `FormattingHandler`; decides if A3 is
  separate from A1.
- Whether `IdeSupportGherkinParser` error recovery keeps feature-level tags when a French file is parsed as
  English. This decides how wrong `FeatureTagIndex` counts are today, not whether it needs the fix.
