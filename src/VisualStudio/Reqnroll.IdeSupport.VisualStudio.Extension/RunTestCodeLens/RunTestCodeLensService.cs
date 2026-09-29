#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.VisualStudio.Extension.NavigationBar;
using Reqnroll.IdeSupport.VisualStudio.Extension.TestTargets;
using Reqnroll.IdeSupport.VisualStudio.NavigationBar;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.RunTestCodeLens;

/// <summary>
/// Composes <see cref="GherkinNavigationBarSymbolService"/> (scenario/Outline ranges) and
/// <see cref="ScenarioTestTargetService"/> (per-scenario <c>reqnroll/resolveTestTargets</c>) into
/// the flat <see cref="RunTestTargetEntry"/> list the classic Run CodeLens bridge needs (design doc
/// §5/§6, issue #262), plus the owning project's build-output assembly path — needed for VS Test
/// Explorer's own <see cref="Microsoft.VisualStudio.TestWindow.TestMethodIdentifier"/> — resolved
/// via VS's DTE automation model, since the LSP protocol itself has no notion of it.
/// </summary>
internal sealed class RunTestCodeLensService
{
    private readonly GherkinNavigationBarSymbolService _symbolService;
    private readonly ScenarioTestTargetService _targetService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RunTestCodeLensService> _logger;

    public RunTestCodeLensService(
        GherkinNavigationBarSymbolService symbolService,
        ScenarioTestTargetService targetService,
        IServiceProvider serviceProvider,
        ILogger<RunTestCodeLensService> logger)
    {
        _symbolService = symbolService;
        _targetService = targetService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the single Run-able target at <paramref name="line"/> in <paramref name="fileUri"/>
    /// (issue #495): fetches the symbol tree, finds the one Method-kind (Scenario/Scenario Outline)
    /// node whose header starts on <paramref name="line"/>, and calls
    /// <c>reqnroll/resolveTestTargets</c> for that node alone — never for every other scenario in
    /// the file. Returns an empty list (no Run lens will render) when the owning project or its
    /// output assembly can't be resolved, or when no scenario node starts on <paramref name="line"/>.
    /// </summary>
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsForLineAsync(string fileUri, int line, CancellationToken cancellationToken)
    {
        var symbols = await _symbolService.FetchSymbolsAsync(fileUri, cancellationToken).ConfigureAwait(false);

        var methodNode = CollectMethodNodes(symbols).FirstOrDefault(n => n.SelectionRange.Start.Line == line);
        if (methodNode is not null)
            return await ResolveScenarioTargetsAsync(fileUri, line, methodNode, cancellationToken).ConfigureAwait(false);

        // No Scenario/Outline starts here — check whether it's a Feature/Rule header line instead
        // (issue #744, "Run scenarios"). Every Method-kind node is checked first since it's the
        // overwhelmingly common case and CollectContainerNodes walks the same tree again.
        var containerNode = CollectContainerNodes(symbols).FirstOrDefault(n => n.SelectionRange.Start.Line == line);
        if (containerNode is not null)
            return await ResolveContainerTargetsAsync(fileUri, line, containerNode, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "RunTestCodeLensService: no scenario/Outline/Feature/Rule node starts on line {Line} in {FileUri}.", line, fileUri);
        return Array.Empty<RunTestTargetEntry>();
    }

    /// <summary>
    /// Resolves the single Run-able scenario/Outline target at <paramref name="node"/> — the
    /// original issue #495 behavior, unchanged by issue #744's container support.
    /// </summary>
    private async Task<IReadOnlyList<RunTestTargetEntry>> ResolveScenarioTargetsAsync(
        string fileUri, int line, GherkinSymbolNode node, CancellationToken cancellationToken)
    {
        var outputAssemblyPath = await ResolveOutputAssemblyPathAsync(fileUri, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(outputAssemblyPath))
        {
            _logger.LogDebug(
                "RunTestCodeLensService: could not resolve an output assembly path for {FileUri}; no Run lens will render.", fileUri);
            return Array.Empty<RunTestTargetEntry>();
        }

        var targets = await _targetService
            .ResolveTestTargetsAsync(fileUri, node.SelectionRange, cancellationToken)
            .ConfigureAwait(false);

        // node.Detail carries "Scenario Outline" vs "Scenario" (see GherkinSymbolNode's
        // remarks) — Kind alone collapses both to the same LSP SymbolKind.Method value.
        var isScenarioOutline = string.Equals(node.Detail, "Scenario Outline", StringComparison.Ordinal);

        var result = targets
            .Select(target => new RunTestTargetEntry(line, outputAssemblyPath!, target.DeclaringTypeFullName, target.MethodName, isScenarioOutline))
            .ToList();

        // Distinct() guards against logging the identical entry twice back-to-back: a Scenario
        // Outline row and its own scenario can resolve to the same (line, assembly, type, method)
        // tuple, which otherwise produced two adjacent, indistinguishable log lines for what a
        // reader would reasonably assume was one entry.
        foreach (var entry in result.Distinct())
        {
            _logger.LogDebug(
                "RunTestCodeLensService: RunTestTargetEntry line={Line} assembly={OutputAssemblyPath} type={DeclaringTypeFullName} method={MethodName} isScenarioOutline={IsScenarioOutline}",
                entry.Line, entry.OutputAssemblyPath, entry.DeclaringTypeFullName, entry.MethodName, entry.IsScenarioOutline);
        }

        return result;
    }

    /// <summary>
    /// Resolves every Run-able target under a Feature/Rule <paramref name="node"/> in one
    /// <c>reqnroll/resolveContainerTestTargets</c> call (issue #744, "Run scenarios") — reuses the
    /// exact same <see cref="RunTestTargetEntry"/> shape and the same downstream
    /// <c>RunTestCodeLensDataPoint</c>/Test Explorer execution path as a single scenario's Run,
    /// just with a broader target set. <see cref="RunTestTargetEntry.IsScenarioOutline"/> is set
    /// <see langword="true"/> for every entry so the CodeLens label reads "Run Scenarios" (plural),
    /// matching the existing row-tests-Outline wording rather than introducing a third label.
    /// </summary>
    private async Task<IReadOnlyList<RunTestTargetEntry>> ResolveContainerTargetsAsync(
        string fileUri, int line, GherkinSymbolNode node, CancellationToken cancellationToken)
    {
        var outputAssemblyPath = await ResolveOutputAssemblyPathAsync(fileUri, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(outputAssemblyPath))
        {
            _logger.LogDebug(
                "RunTestCodeLensService: could not resolve an output assembly path for {FileUri}; no Run lens will render.", fileUri);
            return Array.Empty<RunTestTargetEntry>();
        }

        // node.Range (the container's full body), not node.SelectionRange (its header line alone) —
        // the server resolves every scenario/Outline tag fully contained within the range given.
        var targets = await _targetService
            .ResolveContainerTestTargetsAsync(fileUri, node.Range, cancellationToken)
            .ConfigureAwait(false);

        var result = targets
            .Select(target => new RunTestTargetEntry(line, outputAssemblyPath!, target.DeclaringTypeFullName, target.MethodName, IsScenarioOutline: true))
            .ToList();

        foreach (var entry in result.Distinct())
        {
            _logger.LogDebug(
                "RunTestCodeLensService: container RunTestTargetEntry line={Line} assembly={OutputAssemblyPath} type={DeclaringTypeFullName} method={MethodName}",
                entry.Line, entry.OutputAssemblyPath, entry.DeclaringTypeFullName, entry.MethodName);
        }

        return result;
    }

    /// <summary>
    /// Fetches every Run-lens tag placement for <paramref name="fileUri"/> (issue #495): the
    /// symbol-tree walk alone, with no <c>reqnroll/resolveTestTargets</c> calls at all. Used by
    /// <c>RunTestCodeLensTaggerProvider</c>, which only needs to know which lines get a tag and a
    /// cheap change-detection key — the actual resolved target(s) are only fetched lazily, per
    /// visible line, via <see cref="GetTargetsForLineAsync"/> when that line's own CodeLens data
    /// point is created. Splitting these two concerns is what keeps this feature's cost
    /// proportional to the number of currently-visible lines rather than the whole document (a
    /// 2,000+ scenario file used to make every refresh call the resolver once per scenario).
    /// </summary>
    public async Task<IReadOnlyList<RunTestLensLocation>> GetTagLocationsAsync(string fileUri, CancellationToken cancellationToken)
    {
        var symbols = await _symbolService.FetchSymbolsAsync(fileUri, cancellationToken).ConfigureAwait(false);

        var methodLocations = CollectMethodNodes(symbols)
            .Select(node => new RunTestLensLocation(node.SelectionRange.Start.Line, BuildLensKey(node)));

        // Feature/Rule header lines get their own "Run scenarios" lens too (issue #744) — always a
        // distinct line from any Scenario/Outline header, so no key collision with methodLocations.
        var containerLocations = CollectContainerNodes(symbols)
            .Select(node => new RunTestLensLocation(node.SelectionRange.Start.Line, BuildContainerLensKey(node)));

        return methodLocations.Concat(containerLocations).ToList();
    }

    /// <summary>
    /// Opaque per-node key for <see cref="RunTestLensLocation.Key"/> — changes whenever the
    /// scenario's own identity (name) or Scenario/Outline kind changes, which is all the classic
    /// CodeLens engine needs to decide whether to recreate the line's data point.
    /// </summary>
    private static string BuildLensKey(GherkinSymbolNode node) => $"{node.Detail}|{node.Name}";

    /// <summary>Opaque per-node key for a Feature/Rule "Run scenarios" lens (issue #744) — Kind stands in for Detail, which server-side Feature/Rule symbols never set.</summary>
    private static string BuildContainerLensKey(GherkinSymbolNode node) => $"container:{node.Kind}|{node.Name}";

    /// <summary>
    /// Recursively collects Method-kind (Scenario/Scenario Outline) nodes at any nesting depth —
    /// Rule (Namespace-kind) children included. Mirrors the VS Code extension's <c>collectMethodSymbols</c>.
    /// </summary>
    internal static List<GherkinSymbolNode> CollectMethodNodes(IReadOnlyList<GherkinSymbolNode> symbols)
    {
        const int methodKind = 6; // LSP SymbolKind.Method (DocumentSymbolHandler.cs's ToSymbolKind)
        var result = new List<GherkinSymbolNode>();
        foreach (var node in symbols)
        {
            if (node.Kind == methodKind)
                result.Add(node);
            if (node.Children.Count > 0)
                result.AddRange(CollectMethodNodes(node.Children));
        }
        return result;
    }

    /// <summary>
    /// Recursively collects Feature (root) and Rule (nested) nodes at any depth — the "Run
    /// scenarios" containers (issue #744). Mirrors <see cref="CollectMethodNodes"/>'s shape;
    /// Feature maps to LSP <c>SymbolKind.Module</c> and Rule to <c>SymbolKind.Namespace</c>
    /// (<c>DocumentSymbolHandler.ToSymbolKind</c>) since both collapse Feature/Rule to those
    /// standard kinds rather than a custom one.
    /// </summary>
    internal static List<GherkinSymbolNode> CollectContainerNodes(IReadOnlyList<GherkinSymbolNode> symbols)
    {
        const int moduleKind = 2;    // LSP SymbolKind.Module — Feature
        const int namespaceKind = 3; // LSP SymbolKind.Namespace — Rule
        var result = new List<GherkinSymbolNode>();
        foreach (var node in symbols)
        {
            if (node.Kind == moduleKind || node.Kind == namespaceKind)
                result.Add(node);
            if (node.Children.Count > 0)
                result.AddRange(CollectContainerNodes(node.Children));
        }
        return result;
    }

    private async Task<string?> ResolveOutputAssemblyPathAsync(string fileUri, CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        string filePath;
        try
        {
            filePath = new Uri(fileUri).LocalPath;
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (_serviceProvider.GetService(typeof(DTE)) is not DTE dte)
            return null;

        try
        {
            var project = TryGetContainingProjectFromActiveDocument(dte, filePath)
                ?? dte.Solution.FindProjectItem(filePath)?.ContainingProject;
            return project is null ? null : VsUtils.GetOutputAssemblyPath(project);
        }
        catch (OperationCanceledException)
        {
            // Benign: a fresh reqnroll/refreshCodeLens invalidated this data point while this
            // lookup was still in flight (issue #679) -- VS re-requests the label on its own, so
            // this isn't a failure worth surfacing to the output pane.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunTestCodeLensService: failed to resolve the owning project's output assembly path for {FilePath}.", filePath);
            return null;
        }
    }

    /// <summary>
    /// Prefers the currently active document's own <see cref="ProjectItem.ContainingProject"/> over
    /// <c>Solution.FindProjectItem(path)</c>, which returns an arbitrary match when the same physical
    /// file is linked into more than one project (issue #262 live testing — a <c>.feature</c> file
    /// linked via <c>&lt;ReqnrollFeatureFile Include="..\Other\Foo.feature"&gt;</c> resolves to
    /// whichever project DTE enumerates first, not necessarily the one whose tab is actually open,
    /// sending Run CodeLens to the wrong project's build output). Only used when the active document
    /// is in fact <paramref name="filePath"/> — falls back to <c>FindProjectItem</c> otherwise (e.g. a
    /// background/non-focused tab).
    /// </summary>
    private static Project? TryGetContainingProjectFromActiveDocument(DTE dte, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var activeDocument = dte.ActiveDocument;
        if (activeDocument is null)
            return null;

        if (!string.Equals(activeDocument.FullName, filePath, StringComparison.OrdinalIgnoreCase))
            return null;

        return activeDocument.ProjectItem?.ContainingProject;
    }
}
