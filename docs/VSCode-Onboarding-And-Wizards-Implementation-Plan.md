# VS Code Onboarding, New-Project/New-Item Wizards & Templates — Design and Implementation Plan

> **Status:** Draft for review
> **Audience:** Core team contributors
> **Triggered by:** [#9 · VS Code: onboarding/welcome UX](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/9)
> **Related design (as-designed, pre-implementation):** [F19 · New Project / Item Wizards](LSP-IDE-Support-Feature-Designs.md#f19--new-project--item-wizards), [F20 · Installation & Upgrade Experience](LSP-IDE-Support-Feature-Designs.md#f20--installation--upgrade-experience)
> **Related, already implemented (VS side):** `Reqnroll.IdeSupport.VisualStudio.Wizards[.Core|.UI]` — New Project wizard, Feature File wizard, Config File wizard, ported from the legacy `Reqnroll.VisualStudio` extension

---

## 1. Nature of the changes

Issue #9 is scoped narrowly to onboarding: the VS Code extension has no first-install/upgrade welcome experience, unlike VS's `WelcomeService` (invoked from `ReqnrollPluginPackage`). Investigating it surfaced a wider gap worth planning together, since both fall under the same "guided setup" umbrella and were originally scoped as a pair in the Feature Designs doc (F19 New Project/Item Wizards, F20 Installation & Upgrade Experience):

- **F20 (onboarding)** — as originally designed, VS Code was expected to need only "the native Walkthroughs contribution point ... and the marketplace's built-in release-notes (`CHANGELOG.md`) surface — no custom UI required." **Neither exists today.** There is no `contributes.walkthroughs` entry, no `CHANGELOG.md`, and no install/upgrade telemetry (`ExtensionInstalled`/`ExtensionUpgraded`) wired into `src/VSCode/src/telemetry.ts`.
- **F19 (wizards)** — as originally designed, VS Code's box in the IDE support matrix reads "❌ N/A (snippets instead)," on the assumption that a bundled snippet would cover feature-file scaffolding. **No snippet was ever shipped** (`package.json` has no `contributes.snippets`), and nothing at all exists for "New Project" or "New Config File" equivalents. Meanwhile the VS side of F19 is now fully built: `Reqnroll.IdeSupport.VisualStudio.Wizards` (VS SDK glue), `.Wizards.Core` (`ReqnrollProjectTemplateWizard`, `FeatureFileTemplateWizard`, `ConfigFileTemplateWizard` — IDE-agnostic logic), and `.Wizards.UI` (WPF dialog), with unit-test coverage wired into CI as of [PR #51](https://github.com/reqnroll/Reqnroll.IdeSupport/pull/51).

This document treats both gaps together because a contributor picking up "VS Code parity for new-user setup" needs one plan, not two half-plans that each assume the other doesn't exist. It proposes a design, not a redesign — nothing here contradicts F19/F20's original technology choices, it fills in the concrete implementation those sections deferred.

**Out of scope:** Rider. It has its own idiomatic mechanisms (Live Templates, plugin "What's New") per F19/F20 and is not part of this repo's near-term work.

---

## 2. Current state inventory

| Concern | Visual Studio | VS Code |
|---|---|---|
| First-install / upgrade welcome | `WelcomeService`, invoked by `ReqnrollPluginPackage` (ported from legacy `Reqnroll.VisualStudio`) | **None** |
| "What's new" on upgrade | Same `WelcomeService`, version-gated | **None** — no `CHANGELOG.md` |
| Install/upgrade telemetry | Fired via `ITelemetryService` from `WelcomeService`'s version-check path | **None** — `telemetry.ts` has no `ExtensionInstalled`/`ExtensionUpgraded` events |
| New Project wizard | `ReqnrollProjectTemplateWizard` (`.Wizards.Core`) + WPF `IWizardDialogService` dialog (framework, unit-test-framework choice) → `.vstemplate` replacement tokens | **None** |
| New Item: Feature File | `FeatureFileTemplateWizard` — sets `CustomTool=ReqnrollSingleFileGenerator` / `BuildAction=ReqnrollEmbeddedFeature` item metadata, warns if `Reqnroll.Tools.MsBuild.Generation` is missing | **None** — not even a snippet, despite F19 assuming one |
| New Item: Config File | `VsReqnrollConfigFileWizard` / `ConfigFileTemplateWizard` — scaffolds `reqnroll.json` | **None** |
| Wizard telemetry | `IWizardTelemetry`: `OnFeatureFileAdded`, `OnConfigFileAdded`, `OnProjectTemplateWizardStarted`, `OnProjectTemplateWizardCompleted` | **None** |

The asymmetry is real, not just undocumented: VS's wizard stack is a fully-layered, unit-tested subsystem (`Wizards` → `Wizards.Core` → `Wizards.UI`), while VS Code has zero surface area for any of these flows.

---

## 3. Platform survey — VS Code's building blocks

VS Code has no single analogue of MSBuild `.vstemplate` + item-metadata wizards; it isn't a project-system-driven IDE. The relevant building blocks, and which need maps to which:

| Option | Mechanism | Best fit |
|---|---|---|
| `contributes.walkthroughs` | Multi-step "Get Started" page in the Welcome view, checkable steps, per-step commands | Onboarding (F20) |
| First-run/upgrade notification | `vscode.window.showInformationMessage()` on `activate()`, gated by comparing `context.globalState` version to `extension.packageJSON.version` | Onboarding (F20) — the actual "you just installed/upgraded" moment |
| `CHANGELOG.md` + a "What's New" command that opens it | Bundled markdown, opened via a command triggered from the update notification | Onboarding (F20) |
| `dotnet new` template package | A real .NET template (separate NuGet package), invoked directly or via the C# Dev Kit's existing "Create .NET Project" flow | New Project (F19) — see §6, this is the one piece **outside this repo's boundary** |
| Command + `showQuickPick`/`showInputBox` chain, shelling to `dotnet new` | `reqnroll.newProject` command drives a guided prompt sequence, then runs `dotnet new` | New Project (F19), thin wrapper around the template package |
| Command + bundled template, direct `vscode.workspace.fs.writeFile` | `reqnroll.newFeatureFile` / `reqnroll.newConfigFile`, prompts for a name, writes a bundled skeleton, opens it | New Item: Feature File, New Item: Config File (F19) |
| `contributes.menus["explorer/context"]` | Right-click "New Reqnroll Feature File..." on a folder | Surfaces the New Item commands where VS users expect "Add New Item" |
| `contributes.snippets` | Prefix-triggered skeleton inside an already-created empty file | Cheap complement to the file-creation commands, not a replacement (matches F19's original assumption, just actually built this time) |
| Webview panel wizard | Full custom HTML multi-step UI, mirroring `WizardWindow`/`WizardViewModel` | Not recommended initially — see §6 |

---

## 4. Recommended design

### 4.1 Onboarding (closes #9 / builds out F20)

1. **`contributes.walkthroughs`** in `src/VSCode/package.json` — one walkthrough, steps covering: opening a `.feature` file, Go to Step Definition, Find Usages, Rename Step, defining missing steps. Each step's `completionEvents` ties to the corresponding command firing at least once, so the checklist self-updates as the user tries things.
2. **`CHANGELOG.md`** at `src/VSCode/CHANGELOG.md` — VS Code's Extensions view surfaces this automatically on the extension's page and (for installed extensions) after an update; no custom rendering needed.
3. **First-run/upgrade notification** — on `activate()`, compare a stored `lastActivatedVersion` in `context.globalState` against `context.extension.packageJSON.version`:
   - No stored version → first install → `showInformationMessage` with actions `["Get Started", "Later"]`; "Get Started" opens the walkthrough (`vscode.commands.executeCommand('workbench.action.openWalkthrough', ...)`).
   - Stored version present and older → upgrade → `showInformationMessage` with actions `["What's New", "Later"]`; "What's New" opens `CHANGELOG.md` (`vscode.commands.executeCommand('markdown.showPreview', changelogUri)` or a plain text open, whichever renders more cleanly).
   - Stored version equals current → no-op.
   - Update the stored version unconditionally after the check.
4. **Install/upgrade telemetry** — fire `ExtensionInstalled` / `ExtensionUpgraded` from the same version-check path in `telemetry.ts`, directly (not via the server's `telemetry/event` notification — per the Architecture doc, these events **must** fire client-side because they occur before/independently of the LSP server; this is a deliberate, documented exception to the otherwise server-driven Q11 telemetry model, not a new architectural decision).

### 4.2 New Item: Feature File / Config File (builds out F19)

Two near-identical commands, each following the same shape:

1. Prompt for a file name via `showInputBox` (default suggestion, basic validation — non-empty, valid filename characters).
2. Write a bundled template (`resources/templates/NewFeature.feature.template`, `resources/templates/reqnroll.json.template`) via `vscode.workspace.fs.writeFile`, substituting the feature/file name where the template needs it.
3. Open the new file in the editor.
4. **Feature File only** — mirror `FeatureFileTemplateWizard`'s missing-package check: if the target project's `.csproj` (resolved via the existing `projectManager.ts`/`msbuildEvaluator.ts` machinery) doesn't reference `Reqnroll.Tools.MsBuild.Generation`, show a `showWarningMessage` pointing at the NuGet package — the VS Code equivalent of `context.ShowProblem`. No item metadata to set (no MSBuild `CustomTool`/`BuildAction` concept applies here; the generation package works off file presence + project reference alone, independent of what created the file).
5. Register both as Command Palette commands (`reqnroll.newFeatureFile`, `reqnroll.newConfigFile`) and under `contributes.menus["explorer/context"]` (`when: explorerResourceIsFolder`, `resourceLangId` filters not applicable since VS Code presents both regardless of clicked-folder content).
6. Fire `OnFeatureFileAdded` / `OnConfigFileAdded`-equivalent telemetry events, matching the VS `IWizardTelemetry` event names for cross-IDE analytics comparability.

### 4.3 New Project (builds out F19, partially outside this repo)

**Recommendation: do not build a custom scaffolding UI in the VS Code extension for this.** VS Code's own convention for .NET project creation is to delegate to `dotnet new` (the C# Dev Kit's "Create .NET Project" flow already works this way), and Reqnroll has no reason to diverge from that convention just to mirror VS's dialog. Two pieces, only one of which belongs in this repository:

1. **A `dotnet new` template package** (e.g. `Reqnroll.Templates`, packaged and published separately — **not** part of `Reqnroll.IdeSupport`) exposing the same parameters `ReqnrollProjectTemplateWizard` currently gathers through its WPF dialog: target framework, unit test framework (MSTest/NUnit/xUnit/xUnit.v3). This does not exist yet anywhere in the Reqnroll organization as far as this investigation found (see §7, open question).
2. **A thin wrapper command** in this repo, `reqnroll.newProject`, that:
   - Confirms the template is installed (`dotnet new list` or attempts `dotnet new reqnroll --dry-run`); if missing, offers to run `dotnet new install Reqnroll.Templates` first.
   - Drives a `showQuickPick`/`showInputBox` sequence for project name, framework, and test-framework choice (functionally replacing the WPF `IWizardDialogService` dialog with VS Code-native prompts).
   - Shells out to `dotnet new reqnroll <args>` via `child_process` or a `vscode.tasks.Task`, then opens the resulting folder (`vscode.commands.executeCommand('vscode.openFolder', ...)`).
   - Fires `OnProjectTemplateWizardStarted` / `OnProjectTemplateWizardCompleted`-equivalent telemetry.

This piece should be sequenced **last** of the three (§5) since it depends on an external deliverable this plan cannot commit on behalf of.

---

## 5. New components

| Component | Project / file | Responsibility |
|---|---|---|
| `onboarding.ts` | `src/VSCode/src/` | Version-check on activate, first-run/upgrade notifications, wires `ExtensionInstalled`/`ExtensionUpgraded` telemetry |
| `contributes.walkthroughs` entry | `src/VSCode/package.json` | Getting-started steps |
| `CHANGELOG.md` | `src/VSCode/` | Upgrade "What's New" surface (also the standard Marketplace changelog tab) |
| `newFileCommands.ts` | `src/VSCode/src/` | `reqnroll.newFeatureFile`, `reqnroll.newConfigFile` command implementations, missing-package check |
| `resources/templates/*.template` | `src/VSCode/resources/templates/` | Bundled feature-file / config-file skeletons |
| `contributes.snippets` entry + `.code-snippets` file | `src/VSCode/package.json`, `src/VSCode/snippets/` | Complementary in-editor scaffolding (actually delivers what F19 originally assumed already existed) |
| `newProject.ts` | `src/VSCode/src/` | `reqnroll.newProject` — template-presence check, guided prompts, `dotnet new` invocation (depends on the external template package, §4.3/§7) |

---

## 6. Explicitly deferred / non-goals

- **No custom Webview wizard UI.** A QuickPick/InputBox chain is sufficient for the parameter set involved (a handful of enum-like choices); a full HTML wizard would be disproportionate engineering for what VS needed WPF for mainly due to VSSDK constraints, not UX necessity.
- **No attempt to replicate VS's modal "Add New Item" dialog affordance.** Command Palette + explorer context menu is the idiomatic VS Code equivalent; users do not expect a blocking dialog there.
- **No MSBuild item-metadata equivalent.** `CustomTool`/`BuildAction` are VS/MSBuild-project-system concepts with no VS Code analogue; the missing-package warning replaces the one behaviorally meaningful part of that wizard (steering users to install the generation package).
- **The `dotnet new` template package itself is not scoped by this plan** — it is a packaging/distribution decision (possibly living in the `Reqnroll` core repo, not `Reqnroll.IdeSupport`) that needs its own owner and issue. This plan only specifies the *interface* the VS Code wrapper command expects from it.

---

## 7. Open questions

1. **Does a `dotnet new` Reqnroll project template already exist anywhere in the organization** (main `Reqnroll` repo, a separate templates repo, NuGet.org)? This investigation found none referenced from `Reqnroll.IdeSupport`, but this repo is not authoritative for that question. If one exists, §4.3 shrinks to "wrapper command only." If not, this needs its own tracking issue before `reqnroll.newProject` can ship.
2. **Where should `ExtensionInstalled`/`ExtensionUpgraded` firing live relative to the rest of `telemetry.ts`'s Q11-resolved `telemetry/event`-relay design?** They're a deliberate carve-out (client-side, pre-server), but should be reviewed against however VS's `WelcomeService` currently distinguishes install-vs-upgrade, so the version-comparison semantics (e.g., downgrade handling, pre-release version strings) match across IDEs for comparable analytics.
3. **Should the walkthrough's step list be curated now or grown iteratively?** Recommend shipping a minimal 3–4 step walkthrough first (open a feature file, go to definition, find usages) rather than trying to enumerate every F-numbered feature up front.

---

## 8. Suggested phasing

1. **Phase 1 — Onboarding** (§4.1): cheapest, no external dependencies, directly closes #9. `contributes.walkthroughs` + `CHANGELOG.md` + first-run/upgrade notification + telemetry.
2. **Phase 2 — New Item commands** (§4.2): feature-file and config-file creation commands + snippets. No external dependencies.
3. **Phase 3 — New Project** (§4.3): blocked on the open question in §7.1; scope the wrapper command once the template package's existence/location is confirmed.

---

## 9. Impact on testing

- **Unit (TS/Mocha, `src/VSCode/src/test/`)** — following the existing pattern in `lsp/projectManager.test.ts`/`commands/renameStep.test.ts`: test the version-comparison logic in `onboarding.ts` (first-install vs. upgrade vs. no-op) against a mocked `Memento`/`globalState`; test template substitution in `newFileCommands.ts` against fixed input; test the missing-package detection logic against a fixed `.csproj` fixture.
- **Integration (`extension.test.ts`)** — assert the new commands (`reqnroll.newFeatureFile`, `reqnroll.newConfigFile`, `reqnroll.newProject` once built) are registered and appear in `vscode.commands.getCommands()`, matching the existing registration-check pattern already used for other commands in that file.
- **Manual verification** — walkthrough rendering, notification/action-button wiring, and `dotnet new` shell-out behavior are not practically covered by the Mocha harness (no VS Code UI automation in this repo's test setup) and should be manually verified per the `/verify` skill before merging each phase, the same way UI-affecting VS Code changes have been verified in prior work (see [[project-vscode-extension-status]]).

---

## 10. Cross-references

- [[project-vscode-extension-status]] — VS Code extension implementation history and outstanding items
- [[vs-package-autoload-pkgdef-generation]] — for context on how VS's own template/wizard packaging (`ItemTemplates`, `ProjectTemplate` VSIX projects) is wired, in case any packaging lessons transfer
- [[feedback-vs-specific-gating]] — not directly applicable here (nothing in this plan is a VS-only workaround), included for contributors checking whether new client-side branching needs the `ClientIdeContext.IsVisualStudio` gate: it does not, since all work here is VS-Code-only files under `src/VSCode/`
