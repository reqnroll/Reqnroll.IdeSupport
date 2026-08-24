using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
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
    private readonly ClientIdeContext _clientIde;

    /// <summary>Initializes a new instance of the <see cref="StepCodeLensHandler"/> class.</summary>
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

        _logger.LogVerbose($"StepCodeLensHandler: {lenses.Count} lens(es) for {uri}");
        return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]>(lenses.ToArray());
    }

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
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, string? _uriStr, int line, int col) =>
        new()
        {
            Range = lens.Range,
            Command = new Command { Title = "0 step usages", Name = "reqnroll.noStepUsages", Arguments = null }
        };

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFile(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
