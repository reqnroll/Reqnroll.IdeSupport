#nullable enable

using System;
using System.Collections.Generic;
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
    /// Resolves every Run-able target in <paramref name="fileUri"/>: fetches the symbol tree, keeps
    /// Method-kind (Scenario/Scenario Outline) nodes at any nesting depth (covers scenarios nested
    /// under a <c>Rule</c>), calls <c>reqnroll/resolveTestTargets</c> for each, and pairs the results
    /// with the owning project's build-output assembly path. Returns an empty list (no Run lens will
    /// render) when the owning project or its output assembly can't be resolved — mirrors the
    /// "not built yet" reasoning the resolver itself already applies server-side.
    /// </summary>
    public async Task<IReadOnlyList<RunTestTargetEntry>> GetTargetsAsync(string fileUri, CancellationToken cancellationToken)
    {
        var symbols = await _symbolService.FetchSymbolsAsync(fileUri, cancellationToken).ConfigureAwait(false);
        var scenarioNodes = CollectMethodNodes(symbols);
        if (scenarioNodes.Count == 0)
            return Array.Empty<RunTestTargetEntry>();

        var outputAssemblyPath = await ResolveOutputAssemblyPathAsync(fileUri, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(outputAssemblyPath))
        {
            _logger.LogInformation(
                "RunTestCodeLensService: could not resolve an output assembly path for {FileUri}; no Run lens will render.", fileUri);
            return Array.Empty<RunTestTargetEntry>();
        }

        _logger.LogInformation(
            "RunTestCodeLensService: resolved output assembly path {OutputAssemblyPath} for {FileUri}.",
            outputAssemblyPath, fileUri);

        var result = new List<RunTestTargetEntry>();
        foreach (var node in scenarioNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targets = await _targetService
                .ResolveTestTargetsAsync(fileUri, node.SelectionRange, cancellationToken)
                .ConfigureAwait(false);

            foreach (var target in targets)
                result.Add(new RunTestTargetEntry(node.SelectionRange.Start.Line, outputAssemblyPath!, target.DeclaringTypeFullName, target.MethodName));
        }

        foreach (var entry in result)
        {
            _logger.LogInformation(
                "RunTestCodeLensService: RunTestTargetEntry line={Line} assembly={OutputAssemblyPath} type={DeclaringTypeFullName} method={MethodName}",
                entry.Line, entry.OutputAssemblyPath, entry.DeclaringTypeFullName, entry.MethodName);
        }

        return result;
    }

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
