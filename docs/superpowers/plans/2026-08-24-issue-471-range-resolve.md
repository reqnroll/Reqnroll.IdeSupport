# Issue #471: Range-Scoped Semantic Tokens/Inlay Hints + Lazy CodeLens Resolve — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the LSP server computing expensive per-item data for CodeLens/SemanticTokens/InlayHint content the client didn't ask for (off-screen lenses, the whole document instead of the visible range), which is the top-ranked, lowest-effort lever identified in the issue #471 investigation for the reported multi-second/multi-tens-of-seconds stalls on large solutions.

**Architecture:** Three independent protocol-shape fixes, landed as separate commits/tasks: (1) `textDocument/semanticTokens/range` currently computes the whole document and discards everything outside the range — make it genuinely range-scoped. (2) `textDocument/inlayHint` filters *output* by range but not *compute* — same fix, smaller scope. (3) `textDocument/codeLens` has no `resolve` support at all, so every lens's expensive count (`StepCodeLensHandler`'s `FindUsages` scan, `HookMatchCountCodeLensHandler`'s scenario-corpus walk) computes eagerly for every binding in the file on every poll — add `codeLens/resolve` and defer that computation to it, gated behind confirmed client support (see Task 9).

**Tech Stack:** C#/.NET 10, OmniSharp.Extensions.LanguageServer 0.19.9, xUnit + NSubstitute + AwesomeAssertions.

**Spec:** GitHub issue [reqnroll/Reqnroll.IdeSupport#471](https://github.com/reqnroll/Reqnroll.IdeSupport/issues/471) — see the three investigation comments (root cause, dispatch mechanism, range/resolve audit) for the full evidence trail this plan implements.

## Global Constraints

- Every existing test in `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests` must keep passing unchanged unless a task explicitly says otherwise — most tasks below are designed to be additive (new optional parameters, new methods) specifically so the existing suite doesn't need touching.
- `dotnet build Reqnroll.IdeSupport.slnx` and `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj` must be clean after every task.
- No behavior change for Visual Studio until Task 9's live verification confirms `codeLens/resolve` actually works there — the CodeLens tasks (6-8) gate the deferred path behind `ClientIdeContext.IsVisualStudio == false`.
- Follow the codebase's existing style: `global::OmniSharp.Extensions.LanguageServer.Protocol.Models.X` qualification where the surrounding file already uses it (avoids ambiguity with the enclosing `Features.CodeLens`/`Features.SemanticTokens` namespaces); build `JObject`/`JArray` payloads inline rather than via record+serialization (matches `Command.Arguments`'s existing style).
- **Out of scope for this plan** (separate follow-up plans, per the issue's posted recommendations): indexing `BindingMatchService.FindUsages` by binding location (bigger data-structure change, see the issue's clangd-index-shape design comment), and getting Roslyn discovery/reparse off the `textDocument/didOpen`/`didChange` critical path (separate scheduling/architecture concern). Do not attempt either as part of this plan.
- **Additional finding surfaced while planning, not fixed here:** `SemanticTokenService.ResolvePosition` (`src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokenService.cs:278-292`) is a linear scan over every line in the document, called twice per tag from `CollectLeafTokens` — an O(tags × document-lines) cost on top of the O(tags) encoding itself. Task 1/2 below reduce how many tags this runs against for `/range` requests (proportional to the visible range instead of the whole document), but `textDocument/semanticTokens/full` still pays the full cost. Worth a dedicated follow-up (binary search or a precomputed line-start offset array) — flagging it here so it isn't lost, not fixing it in this plan.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/ISemanticTokenService.cs` | Modify: add `GetSemanticTokensForRangeAsync` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokenService.cs` | Modify: range-scoped tag filtering before encoding |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokensHandler.cs` | Modify: `/range` handler calls the new range-scoped method |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/InlayHints/IGherkinInlayHintService.cs` | Modify: add optional range params to `Build` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Core/InlayHints/GherkinInlayHintService.cs` | Modify: skip out-of-range steps before building hints |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/InlayHints/InlayHintHandler.cs` | Modify: pass the requested range's line bounds into `Build` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs` | Modify: add `CodeLensResolve` constant |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/Program.cs` | Modify: declare `codeLensProvider.resolveProvider = true` statically |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/StepCodeLensHandler.cs` | Modify: gated eager/deferred split + `ResolveAsync` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/HookMatchCountCodeLensHandler.cs` | Modify: gated eager/deferred split + `ResolveAsync` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/CodeLensResolveHandler.cs` | Create: dispatches `codeLens/resolve` by the lens's `Data.kind` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/ServiceCollectionExtensions.cs` | Modify: register `CodeLensResolveHandler` |
| `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/LanguageServerOptionsExtensions.cs` | Modify: register `codeLens/resolve` routing |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/SemanticTokens/SemanticTokenServiceTests.cs` | Modify: new range-scoping tests |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/InlayHints/GherkinInlayHintServiceTests.cs` | Modify: new range-scoping tests |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/StepCodeLensHandlerTests.cs` | Modify: `CreateSut()` gains a VS-default arg; new deferred-path + resolve tests |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/HookMatchCountCodeLensHandlerTests.cs` | Modify: same shape as above |
| `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/CodeLensResolveHandlerTests.cs` | Create: dispatcher tests |

---

### Task 1: SemanticTokens — genuine range-scoped encoding

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/ISemanticTokenService.cs`
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokenService.cs`
- Test: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/SemanticTokens/SemanticTokenServiceTests.cs`

**Interfaces:**
- Consumes: `IDocumentBufferService.TryGet(DocumentUri, out DocumentBuffer? buffer)` (existing, `buffer.Tags` is `IReadOnlyCollection<DeveroomTag>`), `DeveroomTag.Range` (`GherkinRange`, has `.Start`/`.End` absolute offsets and `.Snapshot`).
- Produces: `ISemanticTokenService.GetSemanticTokensForRangeAsync(DocumentUri uri, int version, global::OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, CancellationToken cancellationToken = default) : Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>` — used by Task 2.

- [ ] **Step 1: Write the failing test**

The existing file's real fixture (verified, not guessed): `CreateSut() => new(_bufferService, _logger)`, `SetupBuffer(DocumentBuffer? buf)` stubs `_bufferService.TryGet(FeatureUri, out _)`, and the established way to build a tag is `new DeveroomTag(DeveroomTagTypes.DefinitionLineKeyword, new GherkinRange(snapshot, startOffset, endOffset))` against a `TestGherkinSnapshot(text)` (the nested test-only snapshot class already in this file), then `new DocumentBuffer(FeatureUri, version, snapshot.GetText()) with { Tags = new[] { tag1, tag2 } }`. Add:

```csharp
[Fact]
public async Task GetSemanticTokensForRangeAsync_excludes_tags_outside_the_requested_line_range()
{
    // "Given x" appears on line 2 and again on line 20 of a repeated Scenario block.
    var text = "Feature: F\n" + string.Concat(Enumerable.Repeat("  Scenario: S\n    Given x\n", 10));
    var snapshot = new TestGherkinSnapshot(text);
    var firstOffset = text.IndexOf("Given x", StringComparison.Ordinal);
    var lastOffset  = text.LastIndexOf("Given x", StringComparison.Ordinal);

    var tag1 = new DeveroomTag(DeveroomTagTypes.DefinitionLineKeyword, new GherkinRange(snapshot, firstOffset, firstOffset + 7));
    var tag2 = new DeveroomTag(DeveroomTagTypes.DefinitionLineKeyword, new GherkinRange(snapshot, lastOffset, lastOffset + 7));

    var buf = new DocumentBuffer(FeatureUri, 1, snapshot.GetText()) with { Tags = new[] { tag1, tag2 } };
    SetupBuffer(buf);

    var sut = CreateSut();
    var range = new LspRange(new Position(0, 0), new Position(3, 0)); // covers only the first "Given x" (line 2)

    var result = await sut.GetSemanticTokensForRangeAsync(FeatureUri, 1, range, CancellationToken.None);

    // 5 ints per token (deltaLine, deltaChar, length, type, modifiers) -- only one of the two tags qualifies.
    result!.Data.Length.Should().Be(5);
}
```

Add `using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;` to the test file's usings if not already present (needed to disambiguate from `System.Range`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~GetSemanticTokensForRangeAsync_excludes_tags_outside_the_requested_line_range"`
Expected: FAIL — `ISemanticTokenService` does not contain a definition for `GetSemanticTokensForRangeAsync` (compile error).

- [ ] **Step 3: Add the interface member**

In `ISemanticTokenService.cs`, add after the existing `GetSemanticTokensAsync` declaration:

```csharp
    /// <summary>
    /// Returns semantic tokens for only the given <paramref name="range"/>, encoded fresh from the
    /// current tags on every call (not cached — a range result is a subset of the full-document
    /// cache entry <see cref="GetSemanticTokensAsync"/> maintains, and caching every distinct
    /// viewport range would bloat that cache for no benefit; encoding is now proportional to the
    /// range's tag count instead of the whole document, so recomputing per call is cheap).
    /// Backs <c>textDocument/semanticTokens/range</c> — issue #471: this used to compute and
    /// discard the entire document.
    /// </summary>
    Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensForRangeAsync(
        DocumentUri uri, int version,
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range,
        CancellationToken cancellationToken = default);
```

- [ ] **Step 4: Implement range-scoped encoding in `SemanticTokenService`**

In `SemanticTokenService.cs`, modify the private `Encode` method to accept an optional line-range filter, and add the new public method. First, add a `using` alias near the top (avoids ambiguity with `System.Range`):

```csharp
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
```

Change the `Encode` signature from `private static List<int> Encode(IReadOnlyCollection<DeveroomTag> tags)` to:

```csharp
    private static List<int> Encode(
        IReadOnlyCollection<DeveroomTag> tags, int? startLine = null, int? endLine = null)
    {
        // Collect all leaf tokens in document order (line asc, char asc).
        var entries = new List<(int Line, int Char, int Length, int TypeIdx, int ModBits)>();
        var scopedTags = startLine.HasValue && endLine.HasValue
            ? FilterToLineRange(tags, startLine.Value, endLine.Value)
            : tags;
        CollectLeafTokens(scopedTags, entries);
```

(the rest of the method body is unchanged — sort, `ResolveOverlaps`, delta-encode loop). Then add the filter helper right before `CollectLeafTokens`:

```csharp
    /// <summary>
    /// Filters to tags whose line span overlaps [<paramref name="startLine"/>, <paramref name="endLine"/>]
    /// (both inclusive), before <see cref="CollectLeafTokens"/> runs — the actual cost reduction for
    /// <c>textDocument/semanticTokens/range</c> (issue #471): fewer tags in means fewer
    /// <see cref="ResolvePosition"/> calls, which is itself O(document lines) per call.
    /// </summary>
    private static IEnumerable<DeveroomTag> FilterToLineRange(
        IEnumerable<DeveroomTag> tags, int startLine, int endLine)
    {
        foreach (var tag in tags)
        {
            var (tagStartLine, _) = ResolvePosition(tag.Range, tag.Range.Start);
            var (tagEndLine, _) = ResolvePosition(tag.Range, tag.Range.End);
            if (tagEndLine >= startLine && tagStartLine <= endLine)
                yield return tag;
        }
    }
```

Then add the new public method, placed after `GetSemanticTokensAsync`:

```csharp
    /// <inheritdoc/>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?> GetSemanticTokensForRangeAsync(
        DocumentUri uri, int version, LspRange range, CancellationToken cancellationToken = default)
    {
        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer?.Tags is not { } tags || tags.Count == 0)
        {
            _logger.LogVerbose($"SemanticTokenService: no tags available for {uri} v{version} (range)");
            return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(EmptyTokens);
        }

        var encoded = Encode(tags, range.Start.Line, range.End.Line);
        var tokens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens
        {
            Data = [.. encoded],
            ResultId = $"{uri}@{version}"
        };
        _logger.LogInfo(
            $"SemanticTokenService: encoded {encoded.Count / 5} range token(s) for {uri} v{version} " +
            $"(lines {range.Start.Line}-{range.End.Line})");
        return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(tokens);
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~GetSemanticTokensForRangeAsync_excludes_tags_outside_the_requested_line_range"`
Expected: PASS

- [ ] **Step 6: Run the full server test project to confirm no regressions**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: PASS, same count as before this task plus the new test.

- [ ] **Step 7: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/ISemanticTokenService.cs src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokenService.cs tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/SemanticTokens/SemanticTokenServiceTests.cs
git commit -m "Add genuine range-scoped semantic token encoding (issue #471)"
```

---

### Task 2: SemanticTokens — wire `/range` requests to the new method

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokensHandler.cs`
- Modify: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/SemanticTokens/SemanticTokensHandlerTests.cs`

**Interfaces:**
- Consumes: `ISemanticTokenService.GetSemanticTokensForRangeAsync` (Task 1).
- Produces: nothing new consumed by later tasks.

- [ ] **Step 1: Update the existing test — it currently asserts the bug**

The existing `Handle_Range_delegates_to_token_service` test asserts today's (buggy) behavior — that `/range` calls the full-document `GetSemanticTokensAsync`. Its real current body:

```csharp
[Fact]
public async Task Handle_Range_delegates_to_token_service()
{
    SetupBufferVersion(FeatureUri, 4);
    var expected = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens { Data = ImmutableArray.Create(0, 0, 7, 0, 0) };
    _tokenService.GetSemanticTokensAsync(FeatureUri, 4, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(expected));

    var sut = CreateSut();
    var request = new SemanticTokensRangeParams
    {
        TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 1, 0)
    };
    var result = await sut.HandleAsync(request, CancellationToken.None);
    result.Should().BeSameAs(expected);
}
```

Replace it with:

```csharp
[Fact]
public async Task Handle_Range_delegates_to_the_range_scoped_token_service_method()
{
    SetupBufferVersion(FeatureUri, 4);
    var expected = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens { Data = ImmutableArray.Create(0, 0, 7, 0, 0) };
    var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 1, 0);
    _tokenService.GetSemanticTokensForRangeAsync(FeatureUri, 4, range, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.SemanticTokens?>(expected));

    var sut = CreateSut();
    var request = new SemanticTokensRangeParams
    {
        TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
        Range = range
    };
    var result = await sut.HandleAsync(request, CancellationToken.None);

    result.Should().BeSameAs(expected);
    await _tokenService.DidNotReceive().GetSemanticTokensAsync(Arg.Any<DocumentUri>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~Handle_Range_delegates_to_the_range_scoped_token_service_method"`
Expected: FAIL — compile error, `ISemanticTokenService`/`_tokenService` (an `ISemanticTokenService` substitute) has no `GetSemanticTokensForRangeAsync` until Task 1 lands (if Task 1 already landed first, this instead fails because `SemanticTokensHandler` still calls `GetSemanticTokensAsync`, so `result` won't be `expected` and `DidNotReceive` fails).

- [ ] **Step 3: Implement**

In `SemanticTokensHandler.cs`, replace the body of the `HandleAsync(SemanticTokensRangeParams request, ...)` overload (currently calls `_semanticTokenService.GetSemanticTokensAsync` and returns all tokens with a `// Return all tokens; the client will filter by range.` comment) with:

```csharp
    /// <summary>Handles a <c>textDocument/semanticTokens/range</c> request.</summary>
    public async Task<LspSemanticTokens> HandleAsync(
        SemanticTokensRangeParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentSemanticTokensRange, uri);

        if (!IsFeatureFile(uri)) return EmptyTokens;
        var version = GetCurrentVersion(uri);

        _logger.LogVerbose($"SemanticTokens/range requested for {uri} (version {version})");

        return await _semanticTokenService
            .GetSemanticTokensForRangeAsync(uri, version, request.Range, cancellationToken)
            .ConfigureAwait(false)
            ?? EmptyTokens;
    }
```

Also delete the now-stale `// ── Range ─────────────────────────────────────────────────────────────────` section's `// Return all tokens; the client will filter by range.` comment line above the method (it describes the old, no-op behavior).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~Handle_range_request_calls_GetSemanticTokensForRangeAsync_not_the_full_document_method"`
Expected: PASS

- [ ] **Step 5: Run the full server test project**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/SemanticTokens/SemanticTokensHandler.cs tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/SemanticTokens/
git commit -m "Wire textDocument/semanticTokens/range to genuine range-scoped encoding (issue #471)"
```

---

### Task 3: InlayHint — genuine range-scoped hint building

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Core/InlayHints/IGherkinInlayHintService.cs`
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Core/InlayHints/GherkinInlayHintService.cs`
- Test: `tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/InlayHints/GherkinInlayHintServiceTests.cs`

**Interfaces:**
- Consumes: `StepBindingMatch.Range` (`GherkinRange`, has `.StartLinePosition.Line`/`.EndLinePosition.Line` — confirmed via `StepBindingMatch.Contains`).
- Produces: `IGherkinInlayHintService.Build(FeatureBindingMatchSet matchSet, int? startLine = null, int? endLine = null) : IReadOnlyList<GherkinInlayHint>` — used by Task 4. Backward-compatible: the two new parameters default to `null` (no filtering), so every existing caller/test keeps compiling and behaving identically without changes.

- [ ] **Step 1: Write the failing test**

The existing file's real helpers (verified, not guessed): `CreateSut() => new GherkinInlayHintService()`, `RegistryWith(params ProjectStepDefinitionBinding[] bindings)`, `Binding(string pattern, string method, string[]? paramTypes = null)`, and `MatchSetFor(string text, ProjectBindingRegistry? registry = null)` (parses real Gherkin text via `DeveroomTagParser`). The existing `Multiple_steps_each_produce_their_own_hint_at_their_own_range` test is the closest analog — it builds a two-step feature with "Given step one" on line 2 and "And step two" on line 3 (0-based: line 0 is `Feature: F`, line 1 is `Scenario: S`). Add:

```csharp
[Fact]
public void Build_with_line_range_excludes_steps_outside_the_range()
{
    var registry = RegistryWith(
        Binding("step one", "N.S1"),
        Binding("step two", "N.S2"));
    const string feature = "Feature: F\nScenario: S\n    Given step one\n    And step two\n";

    // "Given step one" is on line 2; "And step two" is on line 3. Restrict to line 2 only.
    var hints = CreateSut().Build(MatchSetFor(feature, registry), startLine: 2, endLine: 2);

    hints.Should().ContainSingle();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Reqnroll.IdeSupport.LSP.Core.Tests.csproj --filter "FullyQualifiedName~Build_with_line_range_excludes_steps_outside_the_range"`
Expected: FAIL — no overload of `Build` takes `startLine`/`endLine` (compile error).

- [ ] **Step 3: Update the interface**

In `IGherkinInlayHintService.cs`, change:

```csharp
    /// <summary>
    /// Builds inlay hints for the steps in the given binding match set. When
    /// <paramref name="startLine"/>/<paramref name="endLine"/> are both given, steps whose line
    /// span doesn't overlap [<paramref name="startLine"/>, <paramref name="endLine"/>] are skipped
    /// before any hint is built for them — the actual cost reduction for a viewport-scoped
    /// <c>textDocument/inlayHint</c> request (issue #471): the caller previously built hints for
    /// every step in the document, then filtered the *output* by range.
    /// </summary>
    IReadOnlyList<GherkinInlayHint> Build(FeatureBindingMatchSet matchSet, int? startLine = null, int? endLine = null);
```

- [ ] **Step 4: Update the implementation**

In `GherkinInlayHintService.cs`, change the `Build` method signature and add the early-skip check as the first line inside the `foreach`:

```csharp
    public IReadOnlyList<GherkinInlayHint> Build(FeatureBindingMatchSet matchSet, int? startLine = null, int? endLine = null)
    {
        var hints = new List<GherkinInlayHint>();

        foreach (var step in matchSet.Steps)
        {
            if (startLine.HasValue && endLine.HasValue
                && (step.Range.EndLinePosition.Line < startLine.Value || step.Range.StartLinePosition.Line > endLine.Value))
                continue;

            var result = step.Result;
```

(the rest of the loop body — `if (result is null) continue;` and everything after — is unchanged).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Reqnroll.IdeSupport.LSP.Core.Tests.csproj --filter "FullyQualifiedName~Build_with_line_range_excludes_steps_outside_the_range"`
Expected: PASS

- [ ] **Step 6: Run the full Core test project**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/Reqnroll.IdeSupport.LSP.Core.Tests.csproj`
Expected: PASS, no regressions (existing single-arg `Build(matchSet)` calls are unaffected — the new params default to `null`, which skips the range check entirely).

- [ ] **Step 7: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Core/InlayHints/IGherkinInlayHintService.cs src/LSP/Reqnroll.IdeSupport.LSP.Core/InlayHints/GherkinInlayHintService.cs tests/LSP/Reqnroll.IdeSupport.LSP.Core.Tests/InlayHints/GherkinInlayHintServiceTests.cs
git commit -m "Add range-scoped step filtering to GherkinInlayHintService.Build (issue #471)"
```

---

### Task 4: InlayHint — pass the requested range into `Build`

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/InlayHints/InlayHintHandler.cs`
- Modify: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/InlayHints/InlayHintHandlerTests.cs`

**Interfaces:**
- Consumes: `IGherkinInlayHintService.Build(matchSet, startLine, endLine)` (Task 3).

- [ ] **Step 1: Write the failing test**

The existing file's class-level `_matchService` and `_hintService` fields are **real objects** (`new BindingMatchService()` and `new GherkinInlayHintService()`, not substitutes — see the existing `Handle_only_returns_hints_that_intersect_the_requested_range` test, which relies on real end-to-end behavior). To observe what arguments `Build` gets called with, this new test constructs its own `InlayHintHandler` with a *substituted* `IGherkinInlayHintService` instead of the class-level real one, reusing the existing `MakeMatch`/`RequestFor` helpers and `FeatureText`'s real line layout (`Feature: F` / `Scenario: S` / `    Given a step` on line 2 / `    And another step` on line 3):

```csharp
[Fact]
public async Task HandleAsync_passes_the_requested_ranges_line_bounds_to_Build()
{
    var hintService = Substitute.For<IGherkinInlayHintService>();
    var sut = new InlayHintHandler(_matchService, _scopeManager, hintService, _logger);

    var step = MakeMatch("N.S1", startOffset: 33, length: 6, pattern: "a step");
    var matchSet = new FeatureBindingMatchSet(FeatureUri.ToString(), ProjectOwner.Unknown, 1, 1, new[] { step });
    _matchService.Store(matchSet);
    hintService.Build(matchSet, 0, 2).Returns(Array.Empty<GherkinInlayHint>());

    var range = new LspRange(new Position(0, 0), new Position(2, 100)); // only line 2 in view
    await sut.HandleAsync(RequestFor(FeatureUri, range), CancellationToken.None);

    hintService.Received(1).Build(matchSet, 0, 2);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~HandleAsync_passes_the_requested_ranges_line_bounds_to_Build"`
Expected: FAIL — `hintService.Build(matchSet, 0, 2)` was never called (the handler still calls the single-arg overload).

- [ ] **Step 3: Implement**

In `InlayHintHandler.cs`, change:

```csharp
        var hints = _hintService.Build(matchSet)
            .Select(ToInlayHint)
            .Where(h => Intersects(h.Position, request.Range))
            .ToList();
```

to:

```csharp
        var hints = _hintService.Build(matchSet, request.Range.Start.Line, request.Range.End.Line)
            .Select(ToInlayHint)
            .Where(h => Intersects(h.Position, request.Range))
            .ToList();
```

Leave the `.Where(h => Intersects(...))` output filter in place as-is — `Build`'s new line-range check is coarser (whole-line overlap) than `Intersects`'s exact-position check, so the output filter is still needed for correctness at the range's edges; it's just filtering a much smaller candidate set now.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~HandleAsync_passes_the_requested_ranges_line_bounds_to_Build"`
Expected: PASS

- [ ] **Step 5: Run the full server test project**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/InlayHints/InlayHintHandler.cs tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/InlayHints/
git commit -m "Scope InlayHintHandler's Build call to the requested range (issue #471)"
```

---

### Task 5: CodeLens resolve — capability declaration + method name constant

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs`
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/Program.cs`
- Test: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Hosting/ProgramTests.cs` (check with `get_symbols_overview` first — if `ApplyStaticInlayHintCapability`-style tests already exist for `Program.ConfigureServer`, add alongside them; if no such test file/pattern exists, skip the test steps for this task and just verify manually via Step 5's full test run plus Task 9's live check)

**Interfaces:**
- Produces: `LspMethodNames.CodeLensResolve = "codeLens/resolve"` — used by Task 8.

- [ ] **Step 1: Add the method name constant**

In `LspMethodNames.cs`, add right after `TextDocumentCodeLens`:

```csharp
    /// <summary>Method name for the <c>codeLens/resolve</c> request.</summary>
    public const string CodeLensResolve = "codeLens/resolve";
```

- [ ] **Step 2: Declare the capability statically**

In `Program.cs`, find the block that calls `ApplyStaticInlayHintCapability(); ApplyStaticFoldingCapability();` (in the `OnStarted`/initialize-response construction method) and add a call to a new `ApplyStaticCodeLensCapability();` alongside them. Add the method itself right after `ApplyStaticFoldingCapability`:

```csharp
            // codeLensProvider.resolveProvider: declared statically for the same
            // dynamic-registration-race reason as inlayHintProvider/foldingRangeProvider above.
            // textDocument/codeLens itself is already always-on for this server (no capability
            // gating needed for the base request); this only adds resolveProvider so clients that
            // support it can defer per-lens computation to codeLens/resolve (issue #471) — see
            // StepCodeLensHandler/HookMatchCountCodeLensHandler, which only actually defer for
            // non-Visual-Studio clients until Task 9 confirms VS's LSP client resolves correctly.
            void ApplyStaticCodeLensCapability()
            {
                response.Capabilities.CodeLensProvider = new CodeLensRegistrationOptions.StaticOptions
                {
                    ResolveProvider = true
                };
            }
```

Add the call `ApplyStaticCodeLensCapability();` in the same statement group as the other `ApplyStatic*Capability()` calls.

- [ ] **Step 3: Run the full server test project**

Run: `dotnet build Reqnroll.IdeSupport.slnx` then `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: clean build, all tests still PASS (this task only adds a new capability flag and an unused-so-far constant — no existing behavior changes).

- [ ] **Step 4: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Protocol/LspMethodNames.cs src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/Program.cs
git commit -m "Declare codeLensProvider.resolveProvider statically (issue #471)"
```

---

### Task 6: `StepCodeLensHandler` — gated eager/deferred split + `ResolveAsync`

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/StepCodeLensHandler.cs`
- Test: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/StepCodeLensHandlerTests.cs`

**Interfaces:**
- Consumes: `ClientIdeContext.IsVisualStudio` (existing, `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/ClientIdeContext.cs`).
- Produces: `StepCodeLensHandler.ResolveAsync(global::OmniSharp...CodeLens lens, CancellationToken ct) : Task<global::OmniSharp...CodeLens>` and the `"stepUsage"` `Data.kind` payload shape — used by Task 8's dispatcher.

- [ ] **Step 1: Write the failing tests**

Add to `StepCodeLensHandlerTests.cs`:

```csharp
[Fact]
public async Task Handle_non_vs_client_returns_placeholder_lens_without_calling_FindUsages()
{
    var csPath  = CsUri.GetFileSystemPath()!;
    var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
    _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));

    var result = await CreateSut(ide: "vscode").HandleAsync(RequestFor(CsUri), CancellationToken.None);

    result![0].Command.Should().BeNull();
    result[0].Data.Should().NotBeNull();
    _matchService.DidNotReceive().FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
}

[Fact]
public async Task ResolveAsync_computes_the_command_from_the_lens_Data()
{
    var csPath  = CsUri.GetFileSystemPath()!;
    var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
    _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
    _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                 .Returns(new[] { StepBindingMatchBuilder.Create(FeatureUri) });

    var placeholder = (await CreateSut(ide: "vscode").HandleAsync(RequestFor(CsUri), CancellationToken.None))![0];
    var resolved = await CreateSut(ide: "vscode").ResolveAsync(placeholder, CancellationToken.None);

    resolved.Command!.Title.Should().Be("1 step usage");
}

[Fact]
public async Task ResolveAsync_falls_back_to_zero_usages_when_the_binding_can_no_longer_be_found()
{
    _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);
    var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
    {
        Range = new LspRange(new Position(4, 0), new Position(4, 0)),
        Data = new JObject
        {
            ["kind"] = "stepUsage",
            ["uri"] = CsUri.ToString(),
            ["sourceFile"] = CsUri.GetFileSystemPath(),
            ["sourceLine"] = 5,
            ["sourceColumn"] = 1,
        }
    };

    var resolved = await CreateSut(ide: "vscode").ResolveAsync(lens, CancellationToken.None);

    resolved.Command!.Title.Should().Be("0 step usages");
}
```

Add `using Newtonsoft.Json.Linq;` and `using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;` to the test file's usings if not already present (check first).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~StepCodeLensHandlerTests"`
Expected: compile errors — `CreateSut(ide:)` overload and `ResolveAsync` don't exist yet.

- [ ] **Step 3: Update `CreateSut()` to accept and default the IDE**

In `StepCodeLensHandlerTests.cs`, change:

```csharp
    private StepCodeLensHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger);
```

to:

```csharp
    private StepCodeLensHandler CreateSut(string ide = "visualstudio") =>
        new(_matchService, _scopeManager, _registryLookup, new ClientIdeContext(ide), _logger);
```

Add `using Reqnroll.IdeSupport.LSP.Server.Hosting;` to the test file's usings if not already present. This one-line change is why every *existing* test in this file keeps passing unmodified: `CreateSut()` with no argument still defaults to `"visualstudio"`, which Task 6 Step 4 below keeps on today's eager, unchanged code path.

- [ ] **Step 4: Implement the split in `StepCodeLensHandler.cs`**

Add the constructor dependency. Change:

```csharp
    public StepCodeLensHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IIdeSupportLogger               logger,
        IOperationDurationRecorder?   recorder = null)
    {
        _matchService   = matchService;
        _scopeManager   = scopeManager;
        _registryLookup = registryLookup;
        _logger         = logger;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }
```

to:

```csharp
    private readonly ClientIdeContext _clientIde;

    public StepCodeLensHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        ClientIdeContext              clientIde,
        IIdeSupportLogger               logger,
        IOperationDurationRecorder?   recorder = null)
    {
        _matchService   = matchService;
        _scopeManager   = scopeManager;
        _registryLookup = registryLookup;
        _clientIde      = clientIde;
        _logger         = logger;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }
```

(add the `_clientIde` field declaration next to the other `private readonly` fields at the top of the class, and add `using Reqnroll.IdeSupport.LSP.Server.Hosting;` to the file's usings.)

Then replace the lens-building loop body. Change:

```csharp
        foreach (var binding in registry.StepDefinitions)
        {
            if (!binding.IsValid) continue;
            var src = binding.Implementation?.SourceLocation;
            if (src is null || string.IsNullOrEmpty(src.SourceFile)) continue;

            if (!IsSameFile(src.SourceFile, filePath)) continue;

            var attrKey = (src.SourceFileLine, src.SourceFileColumn);
            if (!seen.Add(attrKey)) continue;

            var bindingLocation = new SourceLocation(src.SourceFile, src.SourceFileLine, src.SourceFileColumn);
            var usages = _matchService.FindUsages(bindingLocation, projectFilter);
            var count  = usages.Count;

            // LSP positions are 0-based; SourceFileLine/SourceFileColumn are 1-based.
            var line = src.SourceFileLine   - 1;
            var col  = src.SourceFileColumn - 1;

            lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
            {
                Range = new LspRange(new Position(line, col), new Position(line, col)),
                Command = new Command
                {
                    Title     = count == 1 ? "1 step usage" : $"{count} step usages",
                    Name      = count > 0 ? "reqnroll.findStepUsages" : "reqnroll.noStepUsages",
                    Arguments = count > 0
                        ? new JArray(uri.ToString(), line, col)
                        : null
                }
            });
        }
```

to:

```csharp
        // Visual Studio's LSP client hasn't yet had codeLens/resolve support confirmed live
        // (issue #471, follow-up verification task) — keep it on today's eager path so it never
        // regresses to blank/unresolved lenses. Non-VS clients (VS Code, Rider) defer the
        // FindUsages scan to codeLens/resolve instead of running it for every binding on every
        // textDocument/codeLens poll.
        var deferToResolve = !_clientIde.IsVisualStudio;

        foreach (var binding in registry.StepDefinitions)
        {
            if (!binding.IsValid) continue;
            var src = binding.Implementation?.SourceLocation;
            if (src is null || string.IsNullOrEmpty(src.SourceFile)) continue;

            if (!IsSameFile(src.SourceFile, filePath)) continue;

            var attrKey = (src.SourceFileLine, src.SourceFileColumn);
            if (!seen.Add(attrKey)) continue;

            // LSP positions are 0-based; SourceFileLine/SourceFileColumn are 1-based.
            var line = src.SourceFileLine   - 1;
            var col  = src.SourceFileColumn - 1;
            var range = new LspRange(new Position(line, col), new Position(line, col));

            if (deferToResolve)
            {
                lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
                {
                    Range = range,
                    Data = new JObject
                    {
                        ["kind"]         = "stepUsage",
                        ["uri"]          = uri.ToString(),
                        ["sourceFile"]   = src.SourceFile,
                        ["sourceLine"]   = src.SourceFileLine,
                        ["sourceColumn"] = src.SourceFileColumn,
                    }
                });
                continue;
            }

            var bindingLocation = new SourceLocation(src.SourceFile, src.SourceFileLine, src.SourceFileColumn);
            var usages = _matchService.FindUsages(bindingLocation, projectFilter);
            lenses.Add(BuildResolvedLens(range, uri, line, col, usages.Count));
        }
```

Add these two new private members right after `HandleAsync` (before `IsCSharp`):

```csharp
    /// <summary>
    /// Resolves a placeholder lens created above (non-VS clients, deferred path) into its final
    /// <c>Command</c> — backs <c>codeLens/resolve</c> (issue #471). Falls back to the "0 step
    /// usages" shape if the binding can no longer be located (e.g. the file changed between the
    /// initial <c>textDocument/codeLens</c> call and this resolve).
    /// </summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> ResolveAsync(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, CancellationToken cancellationToken)
    {
        var data = lens.Data as JObject;
        var uriStr      = data?["uri"]?.Value<string>();
        var sourceFile  = data?["sourceFile"]?.Value<string>();
        var sourceLine  = data?["sourceLine"]?.Value<int?>();
        var sourceCol   = data?["sourceColumn"]?.Value<int?>();

        if (uriStr is null || sourceFile is null || sourceLine is null || sourceCol is null)
            return Task.FromResult(WithZeroUsages(lens, uriStr, lens.Range.Start.Line, lens.Range.Start.Character));

        var uri = DocumentUri.Parse(uriStr);
        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
            return Task.FromResult(WithZeroUsages(lens, uriStr, lens.Range.Start.Line, lens.Range.Start.Character));

        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker)).ToArray()
            : null;

        var bindingLocation = new SourceLocation(sourceFile, sourceLine.Value, sourceCol.Value);
        var usages = _matchService.FindUsages(bindingLocation, projectFilter);
        return Task.FromResult(BuildResolvedLens(lens.Range, uri, lens.Range.Start.Line, lens.Range.Start.Character, usages.Count));
    }

    private static global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens BuildResolvedLens(
        LspRange range, DocumentUri uri, int line, int col, int count) =>
        new()
        {
            Range = range,
            Command = new Command
            {
                Title     = count == 1 ? "1 step usage" : $"{count} step usages",
                Name      = count > 0 ? "reqnroll.findStepUsages" : "reqnroll.noStepUsages",
                Arguments = count > 0 ? new JArray(uri.ToString(), line, col) : null
            }
        };

    private static global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens WithZeroUsages(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, string? uriStr, int line, int col) =>
        new()
        {
            Range = lens.Range,
            Command = new Command { Title = "0 step usages", Name = "reqnroll.noStepUsages", Arguments = null }
        };
```

(`WithZeroUsages`'s `uriStr` parameter is currently unused inside the body — keep it anyway, it documents at the call site what's known even in the fallback case, and a future enhancement might want to log it. If the compiler warns on the unused parameter, prefix it with `_` instead: `string? _uriStr`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~StepCodeLensHandlerTests"`
Expected: PASS — all pre-existing tests (which call `CreateSut()` with no args, defaulting to `"visualstudio"`) plus the three new ones.

- [ ] **Step 6: Run the full server test project**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: PASS, no regressions anywhere else.

- [ ] **Step 7: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/StepCodeLensHandler.cs tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/StepCodeLensHandlerTests.cs
git commit -m "Add gated deferred-resolve path to StepCodeLensHandler (issue #471)"
```

---

### Task 7: `HookMatchCountCodeLensHandler` — mirror the split

**Files:**
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/HookMatchCountCodeLensHandler.cs`
- Test: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/HookMatchCountCodeLensHandlerTests.cs`

**Interfaces:**
- Consumes: `ClientIdeContext.IsVisualStudio` (existing).
- Produces: `HookMatchCountCodeLensHandler.ResolveAsync(lens, ct)` and the `"hookMatchCount"` `Data.kind` payload shape — used by Task 8's dispatcher.

- [ ] **Step 1: Write the failing tests**

The existing file's real helpers (verified, not guessed): `MakeScopedHook(HookType hookType, string csFile = "/workspace/Hooks.cs", int csLine = 5, int csColumn = 1)` (scope is a fixed `BindingScope { FeatureTitle = "F" }`), `MakeHook(HookType hookType, ...)` (unscoped), `BuildMatchSet(string text, ProjectBindingRegistry registry, string docId)` (parses real Gherkin text via `DeveroomTagParser`), and registries are built with `ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook })` — there is no `MakeRegistry` helper in this file (unlike `StepCodeLensHandlerTests`). Add:

```csharp
[Fact]
public async Task Handle_non_vs_client_defers_scoped_hooks_without_walking_the_corpus()
{
    var hook = MakeScopedHook(HookType.BeforeScenario);
    var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
    _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

    var result = await CreateSut(ide: "vscode").HandleAsync(RequestFor(CsUri), CancellationToken.None);

    result.Should().ContainSingle();
    result[0].Command.Should().BeNull();
    result[0].Data.Should().NotBeNull();
    _matchService.DidNotReceive().GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>());
}

[Fact]
public async Task Handle_non_vs_client_still_resolves_unscoped_hooks_eagerly()
{
    // "all scenarios" needs no corpus walk (issue #403) -- no reason to defer it.
    var hook = MakeHook(HookType.BeforeScenario); // unscoped
    var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
    _registryLookup.GetRegistryForUri(CsUri).Returns(registry);

    var result = await CreateSut(ide: "vscode").HandleAsync(RequestFor(CsUri), CancellationToken.None);

    result.Should().ContainSingle();
    result[0].Command!.Title.Should().Be("all scenarios");
}

[Fact]
public async Task ResolveAsync_computes_the_scenario_count_from_the_lens_Data()
{
    var hook = MakeScopedHook(HookType.BeforeScenario);
    var registry = ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>(), new[] { hook });
    _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
    var matchSet = BuildMatchSet("Feature: F\nScenario: S\n    Given a step\n", registry, FeatureUri.ToString());
    _matchService.GetAll(Arg.Any<IReadOnlyCollection<ProjectOwner>?>()).Returns(new[] { matchSet });

    var placeholder = (await CreateSut(ide: "vscode").HandleAsync(RequestFor(CsUri), CancellationToken.None))[0];
    var resolved = await CreateSut(ide: "vscode").ResolveAsync(placeholder, CancellationToken.None);

    resolved.Command!.Title.Should().Be("1 scenario matched");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~HookMatchCountCodeLensHandlerTests"`
Expected: compile errors — `CreateSut(ide:)` and `ResolveAsync` don't exist yet.

- [ ] **Step 3: Update `CreateSut()`**

In `HookMatchCountCodeLensHandlerTests.cs`, change:

```csharp
    private HookMatchCountCodeLensHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger);
```

to:

```csharp
    private HookMatchCountCodeLensHandler CreateSut(string ide = "visualstudio") =>
        new(_matchService, _scopeManager, _registryLookup, new ClientIdeContext(ide), _logger);
```

Add `using Reqnroll.IdeSupport.LSP.Server.Hosting;` to the test file's usings if not already present.

- [ ] **Step 4: Implement the split in `HookMatchCountCodeLensHandler.cs`**

Add the `ClientIdeContext clientIde` constructor parameter and `_clientIde` field, same pattern as Task 6 Step 4.

Replace the hook-processing loop body. Change:

```csharp
            var attrKey = (src.SourceFileLine, src.SourceFileColumn);
            if (!seen.Add(attrKey)) continue;

            // Unscoped hooks (no [Scope] at all) match every scenario in the project: skip the
            // corpus walk and show a static label rather than an unbounded, uninformative count
            // (issue #403).
            string title;
            if (hook.Scope is null)
            {
                title = "all scenarios";
            }
            else
            {
                var scenarios = HookScenarioMatching.ResolveMatchingScenarios(matchSets, hook);
                var count = scenarios.Count;
                title = count == 1 ? "1 scenario matched" : $"{count} scenarios matched";
            }

            // LSP positions are 0-based; SourceFileLine/SourceFileColumn are 1-based.
            var line = src.SourceFileLine   - 1;
            var col  = src.SourceFileColumn - 1;

            lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
            {
                Range = new LspRange(new Position(line, col), new Position(line, col)),
                Command = new Command
                {
                    Title     = title,
                    Name      = "reqnroll.goToMatchingScenarios",
                    Arguments = new JArray(uri.ToString(), line, col),
                },
            });
```

to:

```csharp
            var attrKey = (src.SourceFileLine, src.SourceFileColumn);
            if (!seen.Add(attrKey)) continue;

            // LSP positions are 0-based; SourceFileLine/SourceFileColumn are 1-based.
            var line = src.SourceFileLine   - 1;
            var col  = src.SourceFileColumn - 1;
            var range = new LspRange(new Position(line, col), new Position(line, col));

            // Unscoped hooks (no [Scope] at all) match every scenario in the project: skip the
            // corpus walk and show a static label rather than an unbounded, uninformative count
            // (issue #403). No reason to ever defer this case -- there's nothing expensive to defer.
            if (hook.Scope is null)
            {
                lenses.Add(BuildResolvedLens(range, uri, line, col, title: "all scenarios"));
                continue;
            }

            if (!_clientIde.IsVisualStudio)
            {
                lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
                {
                    Range = range,
                    Data = new JObject
                    {
                        ["kind"]         = "hookMatchCount",
                        ["uri"]          = uri.ToString(),
                        ["sourceFile"]   = src.SourceFile,
                        ["sourceLine"]   = src.SourceFileLine,
                        ["sourceColumn"] = src.SourceFileColumn,
                    }
                });
                continue;
            }

            var scenarios = HookScenarioMatching.ResolveMatchingScenarios(matchSets, hook);
            var count = scenarios.Count;
            lenses.Add(BuildResolvedLens(range, uri, line, col,
                title: count == 1 ? "1 scenario matched" : $"{count} scenarios matched"));
```

Note `matchSets` (`_matchService.GetAll(projectFilter).ToList()`, computed once before the loop) is now only actually consumed on the VS/eager path or the unscoped-hook path — leave its computation where it is; making it lazy is a nice-to-have, not required for correctness, and this plan should not enlarge its own scope by chasing that.

Add these members after `HandleAsync` (before the existing `Empty`/`IsCSharp`/`IsSameFile` helpers):

```csharp
    /// <summary>
    /// Resolves a placeholder lens created above (non-VS clients, scoped-hook deferred path) into
    /// its final <c>Command</c> — backs <c>codeLens/resolve</c> (issue #471). Falls back to "0
    /// scenarios matched" if the hook can no longer be located.
    /// </summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> ResolveAsync(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, CancellationToken cancellationToken)
    {
        var data = lens.Data as JObject;
        var uriStr     = data?["uri"]?.Value<string>();
        var sourceFile = data?["sourceFile"]?.Value<string>();
        var sourceLine = data?["sourceLine"]?.Value<int?>();
        var sourceCol  = data?["sourceColumn"]?.Value<int?>();

        if (uriStr is null || sourceFile is null || sourceLine is null || sourceCol is null)
            return Task.FromResult(BuildResolvedLens(lens.Range, DocumentUri.Parse(uriStr ?? "file:///unknown"),
                lens.Range.Start.Line, lens.Range.Start.Character, "0 scenarios matched"));

        var uri = DocumentUri.Parse(uriStr);
        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
            return Task.FromResult(BuildResolvedLens(lens.Range, uri, lens.Range.Start.Line, lens.Range.Start.Character, "0 scenarios matched"));

        var hook = registry.Hooks.FirstOrDefault(h =>
            h.Implementation?.SourceLocation is { } loc
            && IsSameFile(loc.SourceFile, sourceFile)
            && loc.SourceFileLine == sourceLine.Value
            && loc.SourceFileColumn == sourceCol.Value);
        if (hook is null)
            return Task.FromResult(BuildResolvedLens(lens.Range, uri, lens.Range.Start.Line, lens.Range.Start.Character, "0 scenarios matched"));

        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker)).ToArray()
            : null;
        var matchSets = _matchService.GetAll(projectFilter).ToList();
        var scenarios = HookScenarioMatching.ResolveMatchingScenarios(matchSets, hook);
        var count = scenarios.Count;

        return Task.FromResult(BuildResolvedLens(lens.Range, uri, lens.Range.Start.Line, lens.Range.Start.Character,
            count == 1 ? "1 scenario matched" : $"{count} scenarios matched"));
    }

    private static global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens BuildResolvedLens(
        LspRange range, DocumentUri uri, int line, int col, string title) =>
        new()
        {
            Range = range,
            Command = new Command { Title = title, Name = "reqnroll.goToMatchingScenarios", Arguments = new JArray(uri.ToString(), line, col) }
        };
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~HookMatchCountCodeLensHandlerTests"`
Expected: PASS.

- [ ] **Step 6: Run the full server test project**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/HookMatchCountCodeLensHandler.cs tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/HookMatchCountCodeLensHandlerTests.cs
git commit -m "Add gated deferred-resolve path to HookMatchCountCodeLensHandler (issue #471)"
```

---

### Task 8: `codeLens/resolve` dispatcher + wiring

**Files:**
- Create: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/CodeLensResolveHandler.cs`
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/ServiceCollectionExtensions.cs`
- Modify: `src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/LanguageServerOptionsExtensions.cs`
- Test: `tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/CodeLensResolveHandlerTests.cs`

**Interfaces:**
- Consumes: `StepCodeLensHandler.ResolveAsync` (Task 6), `HookMatchCountCodeLensHandler.ResolveAsync` (Task 7), `LspMethodNames.CodeLensResolve` (Task 5).

- [ ] **Step 1: Write the failing test**

```csharp
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

public class CodeLensResolveHandlerTests
{
    private readonly StepCodeLensHandler _stepHandler = Substitute.For<StepCodeLensHandler>(); // adjust if StepCodeLensHandler is sealed -- see Step 3 note below

    [Fact]
    public async Task ResolveAsync_routes_stepUsage_kind_to_StepCodeLensHandler()
    {
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new Range(new Position(0, 0), new Position(0, 0)),
            Data = new JObject { ["kind"] = "stepUsage" }
        };
        var expected = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = lens.Range,
            Command = new Command { Title = "1 step usage" }
        };
        _stepHandler.ResolveAsync(lens, Arg.Any<CancellationToken>()).Returns(expected);

        var sut = new CodeLensResolveHandler(_stepHandler, _hookMatchCountHandler);
        var result = await sut.ResolveAsync(lens, CancellationToken.None);

        result.Should().BeSameAs(expected);
    }
}
```

Since `StepCodeLensHandler`/`HookMatchCountCodeLensHandler` are `sealed` classes (confirmed in their source), NSubstitute can't create a substitute for them directly unless `ResolveAsync` is declared on an interface. Before writing this test for real, check whether the codebase's convention for these CodeLens handlers is to introduce a small interface per handler or to keep them as concrete sealed classes resolved via DI (`resolver!.Get<T>()`, as seen in `LanguageServerOptionsExtensions.cs`). Given the established pattern in this file (every CodeLens/InlayHint/etc. handler is a concrete sealed class, never an interface), do **not** introduce new interfaces just for this test — instead write `CodeLensResolveHandlerTests` against **real** `StepCodeLensHandler`/`HookMatchCountCodeLensHandler` instances (constructed the same way `StepCodeLensHandlerTests.CreateSut()` does, with substituted `IBindingMatchService` etc.), asserting end-to-end that a `"stepUsage"`-kind lens produces a step-usage title and a `"hookMatchCount"`-kind lens produces a scenario-count title. Rewrite the test above accordingly:

```csharp
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

public class CodeLensResolveHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    [Fact]
    public async Task ResolveAsync_routes_stepUsage_kind_to_StepCodeLensHandler()
    {
        var uri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
        var stepHandler = new StepCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        var hookHandler = new HookMatchCountCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        _registryLookup.GetRegistryForUri(uri).Returns(ProjectBindingRegistry.Invalid);

        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new Range(new Position(0, 0), new Position(0, 0)),
            Data = new JObject
            {
                ["kind"] = "stepUsage", ["uri"] = uri.ToString(),
                ["sourceFile"] = uri.GetFileSystemPath(), ["sourceLine"] = 5, ["sourceColumn"] = 1,
            }
        };

        var sut = new CodeLensResolveHandler(stepHandler, hookHandler);
        var result = await sut.ResolveAsync(lens, CancellationToken.None);

        result.Command!.Title.Should().Be("0 step usages"); // registry Invalid -> fallback path, but proves stepHandler (not hookHandler) ran
    }

    [Fact]
    public async Task ResolveAsync_unknown_kind_returns_the_lens_unchanged()
    {
        var stepHandler = new StepCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        var hookHandler = new HookMatchCountCodeLensHandler(_matchService, _scopeManager, _registryLookup, new ClientIdeContext("vscode"), _logger);
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new Range(new Position(0, 0), new Position(0, 0)),
            Data = new JObject { ["kind"] = "somethingElse" }
        };

        var sut = new CodeLensResolveHandler(stepHandler, hookHandler);
        var result = await sut.ResolveAsync(lens, CancellationToken.None);

        result.Should().BeSameAs(lens);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~CodeLensResolveHandlerTests"`
Expected: FAIL — `CodeLensResolveHandler` doesn't exist yet (compile error).

- [ ] **Step 3: Create `CodeLensResolveHandler`**

```csharp
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Dispatches a <c>codeLens/resolve</c> request to whichever handler produced the lens, based on
/// the <c>"kind"</c> discriminator each handler embeds in <c>CodeLens.Data</c> (issue #471). Both
/// <see cref="StepCodeLensHandler"/> and <see cref="HookMatchCountCodeLensHandler"/> only ever
/// emit a lens with <c>Data</c> set when they've deferred its <c>Command</c> to resolve (non-VS
/// clients only, see their own remarks) — <see cref="HookCodeLensHandler"/>'s <c>.feature</c>-file
/// lenses are cheap enough to always compute eagerly and never carry <c>Data</c>, so they never
/// reach this dispatcher.
/// </summary>
public sealed class CodeLensResolveHandler
{
    private readonly StepCodeLensHandler          _stepHandler;
    private readonly HookMatchCountCodeLensHandler _hookMatchCountHandler;

    /// <summary>Initializes a new instance of the <see cref="CodeLensResolveHandler"/> class.</summary>
    public CodeLensResolveHandler(StepCodeLensHandler stepHandler, HookMatchCountCodeLensHandler hookMatchCountHandler)
    {
        _stepHandler           = stepHandler;
        _hookMatchCountHandler = hookMatchCountHandler;
    }

    /// <summary>Handles a <c>codeLens/resolve</c> request by routing to the originating handler's own resolve logic.</summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> ResolveAsync(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, CancellationToken cancellationToken)
    {
        var kind = (lens.Data as JObject)?["kind"]?.Value<string>();
        return kind switch
        {
            "stepUsage"       => _stepHandler.ResolveAsync(lens, cancellationToken),
            "hookMatchCount"  => _hookMatchCountHandler.ResolveAsync(lens, cancellationToken),
            _                 => Task.FromResult(lens)
        };
    }
}
```

- [ ] **Step 4: Register `CodeLensResolveHandler` for DI**

In `ServiceCollectionExtensions.cs`, add `.AddSingleton<CodeLensResolveHandler>()` to the same chain that already has `.AddSingleton<StepCodeLensHandler>()`/`.AddSingleton<HookMatchCountCodeLensHandler>()` (`AddReqnrollLspHandlers`).

- [ ] **Step 5: Register the `codeLens/resolve` route**

In `LanguageServerOptionsExtensions.cs`, right after the existing `options.OnRequest<CodeLensParams, CodeLens[]>(LspMethodNames.TextDocumentCodeLens, ...)` block, add:

```csharp
        options.OnRequest<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens, global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>(
            LspMethodNames.CodeLensResolve,
            (lens, ct) => resolver!.Get<CodeLensResolveHandler>().ResolveAsync(lens, ct));
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj --filter "FullyQualifiedName~CodeLensResolveHandlerTests"`
Expected: PASS.

- [ ] **Step 7: Run the full server test project**

Run: `dotnet build Reqnroll.IdeSupport.slnx` then `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Reqnroll.IdeSupport.LSP.Server.Tests.csproj`
Expected: clean build, PASS, no regressions.

- [ ] **Step 8: Run the LSP.Server.Specs integration suite**

Run: `dotnet test tests/LSP/Reqnroll.IdeSupport.LSP.Server.Specs/Reqnroll.IdeSupport.LSP.Server.Specs.csproj`
Expected: PASS, same result as before this plan — the deferred path only activates for non-VS clients, and the Specs harness's own client capabilities determine which path it exercises; if any CodeLens-related spec starts failing, read the failure and check whether the harness needs `resolveProvider`/a non-`"visualstudio"` `--ide` value threaded through — do not silently skip a failing spec.

- [ ] **Step 9: Commit**

```bash
git add src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/CodeLens/CodeLensResolveHandler.cs src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/ServiceCollectionExtensions.cs src/LSP/Reqnroll.IdeSupport.LSP.Server/Hosting/LanguageServerOptionsExtensions.cs tests/LSP/Reqnroll.IdeSupport.LSP.Server.Tests/Features/CodeLens/CodeLensResolveHandlerTests.cs
git commit -m "Wire codeLens/resolve dispatcher (issue #471)"
```

---

### Task 9: Live verification — confirm VS Code/Rider actually resolve, decide on the VS gate

**Not a coding task.** This cannot be automated from this environment (no VS/VS Code/Rider GUI access) — it requires Chris to run each IDE and report back, the same pattern used for the original issue #471 investigation's VS repro.

- [ ] **Step 1: Build and deploy this branch**

Build the server and whichever IDE extension(s) are available to test (VS Code extension at minimum, since it's the reference implementation for `codeLens/resolve`; Rider if convenient).

- [ ] **Step 2: Open a `.cs` step-definitions file with several bindings in VS Code**

Confirm: CodeLens titles ("N step usages") appear correctly, and — this is the actual thing being verified — capture a `reqnroll-vs-inspector`-style or equivalent wire trace (or just watch server PERF logs) showing `codeLens/resolve` requests actually being sent by the client for the visible lenses, and `textDocument/codeLens` itself returning quickly (no `FindUsages` cost in that call).

- [ ] **Step 3: Repeat in Rider if available**

Same check. If Rider's bundled LSP client doesn't call `codeLens/resolve` at all, the lenses will show blank/no title — if that happens, note it; Rider already has its own CodeLens rendering path in `src/Rider` per this codebase's architecture (worth checking whether Rider even routes through the generic `textDocument/codeLens` these handlers serve, or has a separate native implementation — check `src/Rider/src/main/kotlin/com/reqnroll/ide/rider/` for an existing CodeLens provider before assuming this deferred path even applies to Rider).

- [ ] **Step 4: Attempt in Visual Studio, expecting no behavior change**

Since `StepCodeLensHandler`/`HookMatchCountCodeLensHandler` gate the deferred path behind `!_clientIde.IsVisualStudio`, VS should show identical (unchanged, eager) behavior to before this plan. Confirm this — if VS's lenses look different or worse, something about the IDE-detection gate itself is wrong and must be fixed before merging, regardless of resolve support.

- [ ] **Step 5: Decision point**

If Step 2 (and Step 3, if tested) confirm `codeLens/resolve` is actually being called and lenses render correctly and faster:
- File a fast follow-up issue/task: flip the gate from `!_clientIde.IsVisualStudio` to unconditional (or add a VS-specific live test first, then flip) once VS support is separately confirmed — do not flip it speculatively without a VS-side check, per this codebase's established "VS-specific gating rule" (all VS workarounds/differences must be gated and verified, not assumed).

If Step 2 shows `codeLens/resolve` is never called (client doesn't support it):
- The deferred path is dead code for that client today. Leave the gate in place (harmless — those lenses just always carry `Data` uselessly) or narrow it further by client, and open a follow-up issue documenting which clients don't support it, since it changes the value proposition of Tasks 6-8 for that client.

This task has no code changes of its own — it's a go/no-go checkpoint before considering the CodeLens portion of this plan (Tasks 5-8) genuinely complete, separate from Tasks 1-4 (semantic tokens / inlay hint range-scoping) which have no such client-support uncertainty and are safe to ship regardless of this task's outcome.
