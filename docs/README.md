# Developer documentation

Design and architecture documentation for Reqnroll IDE Support, for maintainers and for LLM agents
working in this repository. End-user documentation lives in [`site/`](site/README.md).

## Conventions

These apply to every document here. When you change behaviour, update the relevant doc in the same change.

1. **Describe the system as it is now.** Write what the code does and why it is shaped that way, not how
   it evolved. Do not use development phases, roadmaps, "originally X, later Y" narrative, branch names or
   dated reproduction logs.
2. **No issue or PR numbers as explanation.** State the reason in a sentence. A bare "#123" forces a
   reader out of the repository and may not survive. Link an issue only if it is still open.
3. **Keep history only when it prevents a mistake.** If a maintainer could plausibly repeat a mistake
   without it, keep it as a short *pitfall* or *rejected alternative* next to the design it affects,
   with the failure mode. Otherwise leave it to git history.
4. **Anchor claims in code.** Name the file, type or method that implements a behaviour so a reader can
   check it. If a document and the code disagree, the code wins; fix the document.
5. **Status is one line:** `Implemented`, `Proposed` or `Reference`, followed by anything still open.
   Do not use "Draft for team review", and do not layer revision notes on top of a body that is now wrong.
6. **Prefer small, topic-focused files** that fit in a single read, over one very large document.

## Index

Status shows the state of the document, not of the feature. Documents not yet brought in line with the
conventions above are marked **needs rework**.

| Document | Purpose | Status |
|---|---|---|
| [Test-Outcomes-Pipeline.md](Test-Outcomes-Pipeline.md) | How test results reach the Run CodeLens: reporters, wire format, server store, per-IDE glue | Implemented |
| [LSP-IDE-Support-Overview.md](LSP-IDE-Support-Overview.md) | Goals, high-level architecture | **needs rework** (roadmap and release-plan sections) |
| [LSP-IDE-Support-Architecture.md](LSP-IDE-Support-Architecture.md) | Module design, server internals, IDE clients, cross-cutting concerns | **needs rework** (very large; to be split) |
| [LSP-IDE-Support-Feature-Designs.md](LSP-IDE-Support-Feature-Designs.md) | Per-feature design | **needs rework** (very large; phases and history to remove; to be split) |
| [LSP-IDE-Support-Open-Questions.md](LSP-IDE-Support-Open-Questions.md) | Open questions and risk register | **needs rework** |
| [LSP-Protocol-Extensions.md](LSP-Protocol-Extensions.md) | Custom `reqnroll/*` LSP methods and DTOs | Reference (verify against `CustomLspMethodNames.cs`) |
| [Telemetry-Events-Inventory.md](Telemetry-Events-Inventory.md) | Every telemetry event and its schema | Reference (code wins; see `TelemetryEvents.cs`) |
| [Test-Runner-Integration-Design.md](Test-Runner-Integration-Design.md) | Scenario→test-method mapping, Run/Debug affordance per IDE | **needs rework** (chronological) |
| [VS-LSP-Eager-Activation-Research.md](VS-LSP-Eager-Activation-Research.md) | Getting the VS LSP client to activate early | **needs rework** (research note) |
| [VSCode-Onboarding-And-Wizards-Implementation-Plan.md](VSCode-Onboarding-And-Wizards-Implementation-Plan.md) | VS Code onboarding and wizards | **needs rework** (plan) |
| [LSP-Spec-Gap-Analysis.md](LSP-Spec-Gap-Analysis.md) | LSP features not implemented, and why | Reference (deferred analysis) |
| [architecture/complexity-hotspots.md](architecture/complexity-hotspots.md) | Measured complexity hot spots | Reference (point-in-time measurement) |
| [Archive/](Archive/) | Historical plans and designs; not maintained | Archived |
| [site/](site/README.md) | End-user documentation site (Sphinx) | Maintained separately |
