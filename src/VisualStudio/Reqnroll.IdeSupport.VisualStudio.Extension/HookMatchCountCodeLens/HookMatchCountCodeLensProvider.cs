#nullable enable
#pragma warning disable VSEXTPREVIEW_CODELENS

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToMatchingScenarios;
using Reqnroll.IdeSupport.VisualStudio.Extension.StepCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.HookMatchCountCodeLens;

/// <summary>
/// VS.Extensibility CodeLens provider that shows a "N scenario(s) matched" adornment above each
/// hook-binding attribute in a C# file — issue #373, the inverse of #269's hook-match CodeLens on
/// `.feature` lines.
/// </summary>
/// <remarks>
/// <para>
/// A separate provider from <see cref="StepCodeLensProvider"/> rather than folding this lens kind
/// into it: both providers call the same shared <see cref="StepCodeLensState.Service"/>, whose
/// internal per-file cache (issue #552 follow-up) amortises the <c>textDocument/codeLens</c> round
/// trip across every method-level lens either provider asks for, so a second provider issuing its
/// own request through it adds no new LSP traffic. Keeping the two lens kinds in separate
/// providers/files mirrors the LSP-server-side split (<c>StepCodeLensHandler</c> vs
/// <c>HookMatchCountCodeLensHandler</c>) and keeps the already-shipped step-usages code untouched
/// apart from the command-name filter added alongside this provider (see
/// <see cref="StepCodeLens.StepCodeLensProvider"/>).
/// </para>
/// <para>
/// The server's combined <c>textDocument/codeLens</c> response for a `.cs` file can contain both
/// step-usage lenses (<c>reqnroll.findStepUsages</c> / <c>reqnroll.noStepUsages</c>) and
/// hook-match-count lenses (<c>reqnroll.goToMatchingScenarios</c>) in the same method's attribute
/// window when a <c>[Binding]</c> class mixes step and hook methods — this provider filters to the
/// latter by command name.
/// </para>
/// </remarks>
[VisualStudioContribution]
internal sealed class HookMatchCountCodeLensProvider : ExtensionPart, ICodeLensProvider
{
    private readonly StepCodeLensState _state;
    private readonly GoToMatchingScenariosState _goToState;
    private readonly ILogger<HookMatchCountCodeLensProvider> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the provider over the shared runtime state holders.</summary>
    public HookMatchCountCodeLensProvider(
        StepCodeLensState                        state,
        GoToMatchingScenariosState               goToState,
        ILogger<HookMatchCountCodeLensProvider>  logger,
        ILoggerFactory                            loggerFactory)
    {
        _state         = state;
        _goToState     = goToState;
        _logger        = logger;
        _loggerFactory = loggerFactory;
    }

    // Apply to C# files only.
    /// <inheritdoc />
    public TextViewExtensionConfiguration TextViewExtensionConfiguration => new()
    {
        AppliesTo = [DocumentFilter.FromDocumentType("CSharp")]
    };

    // Provider display name shown in VS Tools > Options > Text Editor > Code Lens.
    /// <inheritdoc />
    public CodeLensProviderConfiguration CodeLensProviderConfiguration =>
        new("Reqnroll Hook Match Count");

    /// <inheritdoc />
    public Task<CodeLens?> TryCreateCodeLensAsync(
        CodeElement        codeElement,
        CodeElementContext context,
        CancellationToken  cancellationToken)
    {
        // Only create lenses for methods; types, properties etc. are ignored.
        if (codeElement.Kind != CodeElementKind.KnownValues.Method)
            return Task.FromResult<CodeLens?>(null);

        var fileUri   = context.Range.Document.Uri;
        // LineNumber is 0-based.
        var startLine = context.Range.Start.GetContainingLine().LineNumber;

        var lens = new HookMatchCountCodeLens(
            _state, _goToState, _loggerFactory.CreateLogger<HookMatchCountCodeLens>(), fileUri, startLine);
        return Task.FromResult<CodeLens?>(lens);
    }
}

/// <summary>
/// A single hook-match-count code lens created for a C# method. Aggregates the "N scenarios
/// matched" label for whichever hook-binding attributes fall within a window just above the
/// method declaration, and navigates to the matching scenarios on click.
/// </summary>
internal sealed class HookMatchCountCodeLens : InvokableCodeLens, IInvalidatableLens
{
    private readonly StepCodeLensState _state;
    private readonly GoToMatchingScenariosState _goToState;
    private readonly ILogger<HookMatchCountCodeLens> _logger;
    private readonly Uri _fileUri;
    private readonly int _methodStartLine;

    // Same rationale as StepCodeLens.AttributeLookahead: StepCodeLensState's method-start-line
    // registry is shared across both providers (populated by StepCodeLensProvider, which always
    // runs for every method regardless of binding kind), so this lens reuses it rather than
    // maintaining a second, duplicate registry.
    private const int AttributeLookahead = 5;

    /// <summary>Creates the lens for a specific method and registers it with the shared state for later invalidation.</summary>
    public HookMatchCountCodeLens(
        StepCodeLensState                state,
        GoToMatchingScenariosState       goToState,
        ILogger<HookMatchCountCodeLens>  logger,
        Uri                              fileUri,
        int                              methodStartLine)
    {
        _state           = state;
        _goToState       = goToState;
        _logger          = logger;
        _fileUri         = fileUri;
        _methodStartLine = methodStartLine;
        _state.RegisterLens(this, fileUri.ToString());
    }

    /// <summary>
    /// Returns the "N scenarios matched" label for this method's hook-binding attribute(s), or an
    /// empty label if the method has none.
    /// </summary>
    public override async Task<CodeLensLabel> GetLabelAsync(
        CodeElementContext context,
        CancellationToken  cancellationToken)
    {
        var service = _state.Service;
        if (service is null)
            return new CodeLensLabel { Text = string.Empty, Tooltip = string.Empty };

        try
        {
            var lenses = await service
                .GetLensesAsync(_fileUri.ToString(), cancellationToken)
                .ConfigureAwait(false);

            var currentStartLine = context.Range.Start.GetContainingLine().LineNumber;
            var nextMethod = _state.GetNextMethodLine(_fileUri.ToString(), currentStartLine);
            var upperBound = nextMethod >= 0 ? nextMethod : currentStartLine + AttributeLookahead;

            var hookLenses = lenses
                .Where(l => l.RangeLine >= currentStartLine && l.RangeLine < upperBound)
                .Where(l => l.CommandName == "reqnroll.goToMatchingScenarios")
                .ToList();

            if (hookLenses.Count == 0)
                return new CodeLensLabel { Text = string.Empty, Tooltip = string.Empty };

            // An unscoped hook (server label "all scenarios", issue #403) matches everything, so
            // it dominates any other count in the same aggregation window — show it as-is rather
            // than folding it into ParseCount's numeric sum (which would silently read it as 0).
            string text;
            if (hookLenses.Any(l => l.Title == AllScenariosLabel))
            {
                text = AllScenariosLabel;
            }
            else
            {
                var totalScenarios = hookLenses.Select(l => ParseCount(l.Title)).Sum();
                text = totalScenarios == 1 ? "1 scenario matched" : $"{totalScenarios} scenarios matched";
            }
            var tooltip = "Reqnroll scenarios matched by this hook";

            _logger.LogInformation(
                "HookMatchCountCodeLens.GetLabelAsync: {Text} for method at line {CurrentStartLine} in {FileUri}",
                text, currentStartLine, _fileUri);
            return new CodeLensLabel { Text = text, Tooltip = tooltip };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "HookMatchCountCodeLens.GetLabelAsync: failed for {FileUri}:{MethodStartLine}", _fileUri, _methodStartLine);
            return new CodeLensLabel { Text = string.Empty, Tooltip = string.Empty };
        }
    }

    /// <summary>
    /// Queries the matching scenarios for the first hook-binding attribute found within this
    /// method's attribute window, and navigates directly (single result) or shows a picker
    /// (multiple results).
    /// </summary>
    public override async Task ExecuteAsync(
        CodeElementContext context,
        IClientContext     clientContext,
        CancellationToken  cancellationToken)
    {
        var goToService = _goToState.Service;
        if (goToService is null)
        {
            _logger.LogWarning(
                "HookMatchCountCodeLens.ExecuteAsync: LSP server not yet initialized — cannot go to matching scenarios.");
            return;
        }

        try
        {
            var lensService = _state.Service;
            if (lensService is null) return;

            var lenses = await lensService
                .GetLensesAsync(_fileUri.ToString(), cancellationToken)
                .ConfigureAwait(false);

            var currentStartLine = context.Range.Start.GetContainingLine().LineNumber;
            var nextMethod = _state.GetNextMethodLine(_fileUri.ToString(), currentStartLine);
            var upperBound = nextMethod >= 0 ? nextMethod : currentStartLine + AttributeLookahead;

            // Use the first (topmost) hook lens in this method's attribute block. The server
            // resolves the exact hook by an exact (line, column) match against the attribute's
            // source location, so both ArgLine and ArgChar must be round-tripped verbatim —
            // hardcoding column 0 here previously made every lookup miss (issue #373 follow-up).
            var firstHook = lenses
                .Where(l => l.RangeLine >= currentStartLine && l.RangeLine < upperBound)
                .Where(l => l.CommandName == "reqnroll.goToMatchingScenarios")
                .OrderBy(l => l.RangeLine)
                .FirstOrDefault();

            if (firstHook is null) return;

            _logger.LogInformation(
                "HookMatchCountCodeLens.ExecuteAsync: invoking go-to-matching-scenarios at {FileUri}:{ArgLine}:{ArgChar}",
                _fileUri, firstHook.ArgLine, firstHook.ArgChar);

            var result = await goToService
                .GoToMatchingScenariosAsync(_fileUri.ToString(), firstHook.ArgLine, firstHook.ArgChar, cancellationToken)
                .ConfigureAwait(false);

            if (result.Scenarios.Count == 0)
            {
                _logger.LogInformation("HookMatchCountCodeLens.ExecuteAsync: no matching scenarios.");
                return;
            }

            var renderer = _state.FindUsagesRenderer;
            if (renderer is null) return;

            // Reuse the same FAR-window rendering pipeline as StepCodeLens's find-usages action
            // (issue #373 follow-up: a picker dialog here was inconsistent with that surface) —
            // map to StepUsageLocation and let FeatureReferencesDataSource read the scenario's own
            // header line from disk as the Code-column text (StepText left null).
            var locations = BuildLocations(result.Scenarios);
            var count     = locations.Count;
            var label     = count == 1 ? "1 matching scenario" : $"{count} matching scenarios";
            await renderer.RenderAsync(label, new FindStepUsages.StepUsagesResult(locations), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HookMatchCountCodeLens.ExecuteAsync: failed.");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _state.UnregisterLens(this, _fileUri.ToString());
    }

    /// <summary>
    /// Called by <see cref="StepCodeLensState.InvalidateLensesForFile"/> to trigger a
    /// fresh call to <see cref="GetLabelAsync"/> on VS's next paint cycle.
    /// </summary>
    public void InvalidateLabel() => Invalidate();

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Server-side static label for unscoped hooks (issue #403) — not a "N scenarios matched" count.
    private const string AllScenariosLabel = "all scenarios";

    private static int ParseCount(string title)
    {
        // Title formats: "1 scenario matched" or "N scenarios matched" or "0 scenarios matched".
        // "all scenarios" (unscoped hooks, issue #403) is handled separately by the caller before
        // this is reached.
        var space = title.IndexOf(' ');
        if (space > 0 && int.TryParse(title.Substring(0, space), out var n))
            return n;
        return 0;
    }

    private static System.Collections.Generic.IReadOnlyList<FindStepUsages.StepUsageLocation> BuildLocations(
        System.Collections.Generic.IReadOnlyList<MatchingScenarioLocation> scenarios)
    {
        var locations = new System.Collections.Generic.List<FindStepUsages.StepUsageLocation>(scenarios.Count);
        foreach (var s in scenarios)
        {
            locations.Add(new FindStepUsages.StepUsageLocation(
                fileUri:   s.Uri,
                startLine: s.StartLine,
                startChar: s.StartChar,
                endLine:   s.StartLine,
                endChar:   s.StartChar));
        }
        return locations;
    }
}
