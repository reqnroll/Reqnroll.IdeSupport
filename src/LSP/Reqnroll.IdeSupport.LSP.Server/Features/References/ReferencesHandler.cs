#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.References;

/// <summary>
/// Handles <c>textDocument/references</c> requests originating from a cursor position in a
/// <c>.cs</c> binding file (Find Step Definition Usages / Find All References).
/// <para>
/// Implements MediatR IRequestHandler to allow automatic routing via AddMediatR,
/// avoiding the need for manual OnRequest delegate registration and IServiceProvider capture.
/// </para>
/// </summary>
/// <remarks>
/// Primary-owner resolution / shared-feature scoping 2B: the scope is restricted to the
/// projects that own the queried <c>.cs</c> file.
/// This prevents cross-project bleed when two projects have step definitions at the same
/// source location (same file name + line in a shared binding class).
/// </remarks>
public sealed class ReferencesHandler
{
    private readonly IBindingMatchService         _matchService;
    private readonly ILspWorkspaceScopeManager    _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly ILspTelemetryService?         _telemetryService;
    private readonly IOperationDurationRecorder    _recorder;

    /// <summary>Initializes a new instance of the <see cref="ReferencesHandler"/> class.</summary>
    public ReferencesHandler(
        IBindingMatchService          matchService,
        ILspWorkspaceScopeManager     scopeManager,
        IProjectBindingRegistryLookup registryLookup,
        IIdeSupportLogger               logger,
        ILspTelemetryService?         telemetryService = null,
        IOperationDurationRecorder?   recorder = null)
    {
        _matchService   = matchService;
        _scopeManager   = scopeManager;
        _registryLookup = registryLookup;
        _logger         = logger;
        _telemetryService = telemetryService;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles a <c>textDocument/references</c> request for step-usage references.</summary>
    public Task<LocationOrLocationLinks> HandleAsync(
        ReferenceParams request,
        CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Performance Verification (Layer 4): time the workspace-wide references search.
        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentReferences, uri);

        if (!IsCSharp(uri))
        {
            _logger.LogVerbose($"ReferencesHandler: ignoring non-.cs URI {uri}");
            return Task.FromResult<LocationOrLocationLinks>(new LocationOrLocationLinks());
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult<LocationOrLocationLinks>(new LocationOrLocationLinks());

        // LSP positions are 0-based; SourceLocation is 1-based.
        var line   = request.Position.Line + 1;
        var column = request.Position.Character + 1;
        var bindingLocation = new SourceLocation(filePath, line, column);

        // Primary-owner resolution / shared-feature scoping 2B: restrict search to the projects
        // that own this .cs file.
        // ResolveOwners returns an empty list only when no project claims the file; in that
        // case pass null to FindUsages so it searches all cached match sets (backward compat).
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var usages = _matchService.FindUsages(bindingLocation, projectFilter);

        if (usages.Count == 0)
        {
            // P1: distinguish "not a binding at this location" from "binding with 0 matching steps".
            // HasBindingAtLocation checks the per-project registries for any binding spanning the
            // query line. The three-state contract (null/empty/locations) is the correct design,
            // but OmniSharp's LocationOrLocationLinks JSON converter does not support null
            // serialization, so both "not a binding" and "0 usages" return an empty response over
            // textDocument/references. The VS client (P2) will use a custom reqnroll/findStepUsages
            // request that can carry the full three-state result.
            var hasBinding = _registryLookup.HasBindingAtLocation(uri, bindingLocation);
            if (!hasBinding)
            {
                _logger.LogVerbose(
                    $"ReferencesHandler: no binding at {filePath}:{line}");
                return Task.FromResult<LocationOrLocationLinks>(new LocationOrLocationLinks());
            }

            _logger.LogVerbose(
                $"ReferencesHandler: binding at {filePath}:{line} has 0 usages");
            SendUsagesTelemetry(0, cancellationToken);
            return Task.FromResult<LocationOrLocationLinks>(new LocationOrLocationLinks());
        }

        _logger.LogVerbose(
            $"ReferencesHandler: {usages.Count} usage(s) for binding at {filePath}:{line}");

        var locations = usages
            .Select(match => new LocationOrLocationLink(new Location
            {
                Uri   = DocumentUri.Parse(match.FeatureDocumentId),
                Range = match.Range.ToLspRange()
            }))
            .ToArray();

        SendUsagesTelemetry(usages.Count, cancellationToken);

        return Task.FromResult<LocationOrLocationLinks>(
            new LocationOrLocationLinks(locations));
    }

    /// <summary>
    /// Sends the same <c>"FindStepDefinitionUsages command executed"</c> event
    /// <see cref="Features.References.FindStepUsagesHandler"/> sends for its own
    /// <c>reqnroll/findStepUsages</c> path (issue #581 finding 3), with a <c>Protocol</c> field
    /// so the two paths — VS Code/Rider's native Find All References via this handler, versus
    /// Visual Studio's custom request — produce one comparable usage-count metric instead of an
    /// undercount that silently excludes two of the three IDE clients.
    /// </summary>
    private void SendUsagesTelemetry(int usagesCount, CancellationToken cancellationToken) =>
        _telemetryService?.SendEvent("FindStepDefinitionUsages command executed", new()
        {
            ["UsagesCount"] = usagesCount,
            ["IsCancelled"] = cancellationToken.IsCancellationRequested,
            ["Protocol"] = "textDocument/references",
        });

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
