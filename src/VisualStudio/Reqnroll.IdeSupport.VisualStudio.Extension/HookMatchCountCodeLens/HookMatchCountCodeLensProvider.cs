#nullable enable
#pragma warning disable VSEXTPREVIEW_CODELENS

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Reqnroll.IdeSupport.Common.Logging;
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
/// into it: <see cref="StepCodeLensService"/> already round-trips the LSP server once per method
/// with no caching, so a second provider issuing its own <c>textDocument/codeLens</c> request adds
/// no new category of inefficiency, and keeping the two lens kinds in separate providers/files
/// mirrors the LSP-server-side split (<c>StepCodeLensHandler</c> vs
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
    // NavigationPickerHelper (shared with GoToHooks) still takes IIdeSupportLogger — resolve the
    // shared DI-registered singleton sink for that one call rather than a second ad hoc logger.
    private readonly IIdeSupportLogger _fileLogger;

    /// <summary>Creates the provider over the shared runtime state holders.</summary>
    public HookMatchCountCodeLensProvider(
        StepCodeLensState                        state,
        GoToMatchingScenariosState               goToState,
        ILogger<HookMatchCountCodeLensProvider>  logger,
        ILoggerFactory                            loggerFactory,
        IIdeSupportLogger                          fileLogger)
    {
        _state         = state;
        _goToState     = goToState;
        _logger        = logger;
        _loggerFactory = loggerFactory;
        _fileLogger    = fileLogger;
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
            _state, _goToState, _loggerFactory.CreateLogger<HookMatchCountCodeLens>(), _fileLogger, fileUri, startLine);
        return Task.FromResult<CodeLens?>(lens);
    }
}

/// <summary>
/// A single hook-match-count code lens created for a C# method. Aggregates the "N scenarios
/// matched" label for whichever hook-binding attributes fall within a window just above the
/// method declaration, and navigates to the matching scenarios on click.
/// </summary>
internal sealed class HookMatchCountCodeLens : InvokableCodeLens
{
    private readonly StepCodeLensState _state;
    private readonly GoToMatchingScenariosState _goToState;
    private readonly ILogger<HookMatchCountCodeLens> _logger;
    private readonly IIdeSupportLogger _fileLogger;
    private readonly Uri _fileUri;
    private readonly int _methodStartLine;

    // Same rationale as StepCodeLens.AttributeLookahead: StepCodeLensState's method-start-line
    // registry is shared across both providers (populated by StepCodeLensProvider, which always
    // runs for every method regardless of binding kind), so this lens reuses it rather than
    // maintaining a second, duplicate registry.
    private const int AttributeLookahead = 5;

    /// <summary>Creates the lens for a specific method.</summary>
    public HookMatchCountCodeLens(
        StepCodeLensState                state,
        GoToMatchingScenariosState       goToState,
        ILogger<HookMatchCountCodeLens>  logger,
        IIdeSupportLogger                  fileLogger,
        Uri                              fileUri,
        int                              methodStartLine)
    {
        _state           = state;
        _goToState       = goToState;
        _logger          = logger;
        _fileLogger      = fileLogger;
        _fileUri         = fileUri;
        _methodStartLine = methodStartLine;
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

            var totalScenarios = hookLenses.Select(l => ParseCount(l.Title)).Sum();
            var text    = totalScenarios == 1 ? "1 scenario matched" : $"{totalScenarios} scenarios matched";
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

            // Use the first (topmost) hook lens in this method's attribute block — its ArgLine is
            // the attribute's exact line; the server resolves the exact hook by (line, col), so we
            // use column 0 here the same way StepCodeLens does for find-usages (attributes start at
            // column 0 in practice; ArgLine already carries any real offset the server needs).
            var firstHook = lenses
                .Where(l => l.RangeLine >= currentStartLine && l.RangeLine < upperBound)
                .Where(l => l.CommandName == "reqnroll.goToMatchingScenarios")
                .OrderBy(l => l.RangeLine)
                .FirstOrDefault();

            if (firstHook is null) return;

            _logger.LogInformation(
                "HookMatchCountCodeLens.ExecuteAsync: invoking go-to-matching-scenarios at {FileUri}:{ArgLine}",
                _fileUri, firstHook.ArgLine);

            var result = await goToService
                .GoToMatchingScenariosAsync(_fileUri.ToString(), firstHook.ArgLine, 0, cancellationToken)
                .ConfigureAwait(false);

            if (result.Scenarios.Count == 0)
            {
                _logger.LogInformation("HookMatchCountCodeLens.ExecuteAsync: no matching scenarios.");
                return;
            }

            var targets = BuildTargets(result.Scenarios);
            await Navigation.NavigationPickerHelper.PickAndNavigateAsync(
                    targets,
                    _fileLogger,
                    promptTitle: "Go to Matching Scenarios",
                    cancellationToken)
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
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int ParseCount(string title)
    {
        // Title formats: "1 scenario matched" or "N scenarios matched" or "0 scenarios matched"
        var space = title.IndexOf(' ');
        if (space > 0 && int.TryParse(title.Substring(0, space), out var n))
            return n;
        return 0;
    }

    private static System.Collections.Generic.IReadOnlyList<Navigation.NavigationTarget> BuildTargets(
        System.Collections.Generic.IReadOnlyList<MatchingScenarioLocation> scenarios)
    {
        var targets = new System.Collections.Generic.List<Navigation.NavigationTarget>(scenarios.Count);
        foreach (var s in scenarios)
        {
            if (!Uri.TryCreate(s.Uri, UriKind.Absolute, out var uri) || !uri.IsFile)
                continue;

            var filePath = uri.LocalPath;
            var fileName = System.IO.Path.GetFileName(filePath);
            var kind     = s.IsOutline ? "Scenario Outline" : "Scenario";
            var name     = string.IsNullOrEmpty(s.ScenarioName) ? "(untitled)" : s.ScenarioName;
            // Display: "[Scenario] Add two numbers (Calculator.feature:5)"  (1-based line for readability)
            var displayText = $"[{kind}] {name} ({fileName}:{s.StartLine + 1})";
            targets.Add(new Navigation.NavigationTarget(displayText, filePath, s.StartLine, s.StartChar));
        }
        return targets;
    }
}
