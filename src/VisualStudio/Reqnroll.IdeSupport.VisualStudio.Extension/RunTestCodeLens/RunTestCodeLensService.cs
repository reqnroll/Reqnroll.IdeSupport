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
        var node = CollectMethodNodes(symbols).FirstOrDefault(n => n.SelectionRange.Start.Line == line);
        if (node is null)
        {
            _logger.LogDebug(
                "RunTestCodeLensService: no scenario/Outline node starts on line {Line} in {FileUri}.", line, fileUri);
            return Array.Empty<RunTestTargetEntry>();
        }

        var outputAssemblyPath = await ResolveOutputAssemblyPathAsync(fileUri, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(outputAssemblyPath))
        {
            _logger.LogInformation(
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

        foreach (var entry in result)
        {
            _logger.LogInformation(
                "RunTestCodeLensService: RunTestTargetEntry line={Line} assembly={OutputAssemblyPath} type={DeclaringTypeFullName} method={MethodName} isScenarioOutline={IsScenarioOutline}",
                entry.Line, entry.OutputAssemblyPath, entry.DeclaringTypeFullName, entry.MethodName, entry.IsScenarioOutline);
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
        return CollectMethodNodes(symbols)
            .Select(node => new RunTestLensLocation(node.SelectionRange.Start.Line, BuildLensKey(node)))
            .ToList();
    }

    /// <summary>
    /// Opaque per-node key for <see cref="RunTestLensLocation.Key"/> — changes whenever the
    /// scenario's own identity (name) or Scenario/Outline kind changes, which is all the classic
    /// CodeLens engine needs to decide whether to recreate the line's data point.
    /// </summary>
    private static string BuildLensKey(GherkinSymbolNode node) => $"{node.Detail}|{node.Name}";

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
