using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Handles the standard <c>textDocument/codeLens</c> request for C# files (step usage count code lens).
/// Returns one lens per step-binding attribute found in the file, annotated
/// with the number of matching feature steps.
/// </summary>
/// <remarks>
/// Registered manually (same pattern as semantic tokens / find step usages) to avoid dynamic
/// registration ambiguity with the C# language server on .cs files.
/// NOTE: Uses global:: qualification for <c>OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens</c>
/// to disambiguate from the enclosing Features.CodeLens namespace.
/// </remarks>
public sealed class StepCodeLensHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder    _recorder;

    /// <summary>Initializes a new instance of the <see cref="StepCodeLensHandler"/> class.</summary>
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

    /// <summary>
    /// Handles a <c>textDocument/codeLens</c> request.
    /// Returns one lens per step-binding attribute in the requested .cs file.
    /// Returns <see langword="null"/> for non-.cs files (falls through to the built-in C# server).
    /// Returns an empty array when the file has no discovered step definitions yet.
    /// </summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]> HandleAsync(CodeLensParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        // Cache size at call time (issue #471 investigation): FindUsages below is an unindexed
        // scan over the whole match-set cache, once per binding in this file, so this operation's
        // cost is expected to track cacheSteps (and this file's binding count) — logging it here
        // lets a climbing-duration pattern be confirmed/quantified from the PERF log directly.
        var (cacheDocs, cacheSteps) = _matchService.GetCacheStats();
        using var _perf = _recorder.Measure(
            LspMethodNames.TextDocumentCodeLens, uri, detail: $"cacheDocs={cacheDocs} cacheSteps={cacheSteps}");

        if (!IsCSharp(uri))
        {
            _logger.LogVerbose($"StepCodeLensHandler: ignoring non-.cs URI {uri}");
            return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]>(Array.Empty<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>());
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]>(Array.Empty<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>());

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid || registry.StepDefinitions.IsEmpty)
        {
            _logger.LogVerbose($"StepCodeLensHandler: no registry or no step definitions for {uri}");
            return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]>(Array.Empty<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>());
        }

        // Restrict usage search to the projects that own this .cs file (primary-owner resolution
        // / shared-feature scoping 2B).
        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        var lenses = new List<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();
        // Deduplicate: the same attribute location may appear in multiple registries (linked files).
        var seen = new HashSet<(int line, int col)>();

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

        _logger.LogVerbose($"StepCodeLensHandler: {lenses.Count} lens(es) for {uri}");
        return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]>(lenses.ToArray());
    }

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFile(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
