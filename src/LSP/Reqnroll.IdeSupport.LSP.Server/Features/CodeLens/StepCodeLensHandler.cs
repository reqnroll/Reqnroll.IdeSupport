using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Handles the standard <c>textDocument/codeLens</c> request for C# files (step usage count code lens).
/// Returns one lens per step-definition method found in the file, annotated with the number of
/// matching feature steps aggregated across every binding attribute on that method.
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
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder    _recorder;
    private readonly ClientIdeContext              _clientIde;

    /// <summary>Initializes a new instance of the <see cref="StepCodeLensHandler"/> class.</summary>
    public StepCodeLensHandler(
        IBindingMatchService          matchService,
        IProjectBindingRegistryLookup registryLookup,
        ClientIdeContext              clientIde,
        IIdeSupportLogger               logger,
        IOperationDurationRecorder?   recorder = null)
    {
        _matchService   = matchService;
        _registryLookup = registryLookup;
        _clientIde      = clientIde;
        _logger         = logger;
        _recorder       = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>
    /// Handles a <c>textDocument/codeLens</c> request.
    /// Returns one lens per step-definition method in the requested .cs file, its count
    /// aggregated across every binding attribute on that method.
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

        // Restrict usage search to the projects that own this .cs file, widened to any other
        // project whose own registry independently reports one of this file's bindings (issue
        // #548) -- see IProjectBindingRegistryLookup.ResolveUsageSearchScope's remarks. Shared
        // with FindStepUsagesHandler so the click-to-navigate command sees the same scope this
        // lens's count does.
        var projectFilter = _registryLookup.ResolveUsageSearchScope(uri);

        var lenses = new List<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();
        // Deduplicate by anchor location: every attribute on a method shares the identical
        // (SourceFileLine, SourceFileColumn) -- the method identifier's own position, per the
        // anchor fix below -- so this also catches the case the dedup originally targeted (the
        // same physical attribute reported redundantly by multiple registries for linked files;
        // same file+line+col there too). One lens per location, not per attribute (issue #552):
        // its usage count must come from the location-based FindUsages overload below, which
        // aggregates every binding anchored at that location, the same aggregate Find Step Usages
        // (FAR) already returns -- looking usages up per-attribute BindingId instead (the #552
        // bug) undercounted a multi-attribute method to just one attribute's own usages.
        var seen = new HashSet<(int line, int col)>();

        // Defer the per-binding FindUsages scan to codeLens/resolve ONLY for clients on the
        // opt-in allowlist in ClientIdeContext.CodeLensResolveCapableIdes — that set is empty
        // today, so every shipped client (VS Code, Rider, Visual Studio) takes the eager path
        // below. None of them issue codeLens/resolve, and a deferred lens simply never renders
        // for them (issue #471; see the allowlist note for the evidence).
        var deferToResolve = _clientIde.SupportsCodeLensResolve;

        foreach (var binding in registry.StepDefinitions)
        {
            if (!binding.IsValid) continue;
            var src = binding.Implementation?.SourceLocation;
            if (src is null || string.IsNullOrEmpty(src.SourceFile)) continue;

            if (!IsSameFile(src.SourceFile, filePath)) continue;

            // Anchor on the method identifier's own line (SourceLocation.SourceFileLine), matching
            // the conventional CodeLens-anchor position every client (VS Code, VS, Rider) expects
            // for a "N references"-style lens: rendered directly above the declaration line, the
            // same line the built-in C# references CodeLens targets. This used to be imprecise for
            // connector-discovered bindings specifically -- SourceFileLine came from a raw PDB
            // sequence point, which can land a line or more into the method body rather than on
            // the declaration itself -- but ConnectorDiscoveryService now backfills the exact
            // AST-based method-identifier location the same way Roslyn discovery always has
            // (issue #471 follow-up), so this is precise for both discovery paths again. (An
            // earlier fix here anchored on the *attribute's* own line instead, which rendered
            // correctly in Visual Studio but one line too high in VS Code, whose CodeLens always
            // renders as a floating row above its anchor line rather than overlaid on it.)
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
                        ["bindingId"]    = BindingId.For(binding).ToString(),
                        ["sourceFile"]   = src.SourceFile,
                        ["sourceLine"]   = src.SourceFileLine,
                        ["sourceColumn"] = src.SourceFileColumn,
                    }
                });
                continue;
            }

            // Location-based lookup, not the surviving binding's own BindingId: this location may
            // anchor several attributes (issue #552), and FindUsages(SourceLocation, ...) resolves
            // every BindingId whose range covers it, aggregating their usages into one count -- the
            // same aggregate query Find Step Usages (FAR) already uses for this location.
            var bindingLocation = new SourceLocation(src.SourceFile, src.SourceFileLine, src.SourceFileColumn);
            var usages = _matchService.FindUsages(bindingLocation, projectFilter);
            LogDuplicateUsageDiagnostics(bindingLocation, usages);
            lenses.Add(BuildResolvedLens(range, uri, line, col, usages.Count));
        }

        _logger.LogVerbose($"StepCodeLensHandler: {lenses.Count} lens(es) for {uri}");
        return Task.FromResult<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]>(lenses.ToArray());
    }

    /// <summary>
    /// Resolves a placeholder lens created above (allowlisted resolve-capable clients only — see
    /// <see cref="ClientIdeContext.SupportsCodeLensResolve"/>) into its final <c>Command</c> —
    /// backs <c>codeLens/resolve</c> (issue #471). Falls back to the non-actionable "0 step
    /// usages" shape if the binding can no longer be located (e.g. the file changed between the
    /// initial <c>textDocument/codeLens</c> call and this resolve).
    /// </summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens> ResolveAsync(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens, CancellationToken cancellationToken)
    {
        var data = lens.Data as JObject;
        var uriStr      = data?["uri"]?.Value<string>();
        var bindingIdStr = data?["bindingId"]?.Value<string>();
        var sourceFile  = data?["sourceFile"]?.Value<string>();
        var sourceLine  = data?["sourceLine"]?.Value<int?>();
        var sourceCol   = data?["sourceColumn"]?.Value<int?>();

        if (uriStr is null)
            return Task.FromResult(WithZeroUsages(lens));

        var uri = DocumentUri.Parse(uriStr);
        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid)
            return Task.FromResult(WithZeroUsages(lens));

        // See HandleAsync's remarks (issue #548): the same widened scope applies here regardless
        // of which branch below resolves the binding.
        var projectFilter = _registryLookup.ResolveUsageSearchScope(uri);

        // Prefer the BindingId stashed at lens-creation time (issue #471): a direct O(1)
        // reverse-index lookup, no location math. Fall back to the SourceLocation-based path only
        // for a payload that predates this field (e.g. a stale client-cached lens).
        IReadOnlyList<StepBindingMatch> usages;
        if (bindingIdStr is not null && BindingId.TryParse(bindingIdStr, out var bindingId))
        {
            usages = _matchService.FindUsages(bindingId, projectFilter);
        }
        else if (sourceFile is not null && sourceLine is not null && sourceCol is not null)
        {
            var bindingLocation = new SourceLocation(sourceFile, sourceLine.Value, sourceCol.Value);
            usages = _matchService.FindUsages(bindingLocation, projectFilter);
        }
        else
        {
            return Task.FromResult(WithZeroUsages(lens));
        }

        return Task.FromResult(BuildResolvedLens(lens.Range, uri, lens.Range.Start.Line, lens.Range.Start.Character, usages.Count));
    }

    /// <summary>
    /// Issue #554 guard: a step-usage count is inflated when the same feature step (same document
    /// URI + same line) comes back more than once from the reverse index. That can only happen
    /// when one feature document's match set is indexed twice — an orphaned set left behind by a
    /// concurrent <c>Store</c>, or two live cache entries for the same document — neither of
    /// which is visible in the count alone. <c>BindingMatchService</c> now serialises its writes
    /// so this should never fire; if it ever does, the log says which of the two it is, via
    /// <see cref="IBindingMatchService.AuditIndexConsistency"/>.
    /// Detection is one pass with an early exit and no allocation beyond the seen-set (issue #491:
    /// this runs per binding on the <c>textDocument/codeLens</c> sweep); the expensive grouping and
    /// the audit only run once a duplicate is actually present.
    /// </summary>
    private void LogDuplicateUsageDiagnostics(
        SourceLocation bindingLocation, IReadOnlyList<StepBindingMatch> usages)
    {
        if (usages.Count < 2)
            return;

        var seen = new HashSet<(string Uri, int Line)>(usages.Count, StepLocationComparer.Instance);
        var hasDuplicate = false;
        foreach (var usage in usages)
        {
            if (seen.Add((usage.FeatureDocumentId, usage.Range.StartLinePosition.Line)))
                continue;

            hasDuplicate = true;
            break;
        }

        if (!hasDuplicate)
            return;

        var duplicates = usages
            .GroupBy(u => (Uri: u.FeatureDocumentId, Line: u.Range.StartLinePosition.Line),
                     StepLocationComparer.Instance)
            .Where(g => g.Count() > 1);

        foreach (var duplicate in duplicates)
            _logger.LogWarning(
                $"[DIAG-554] binding at {bindingLocation.SourceFile}:{bindingLocation.SourceFileLine} counts " +
                $"the same feature step {duplicate.Count()} times: {duplicate.Key.Uri} " +
                $"line {duplicate.Key.Line} (projects: " +
                $"{string.Join(", ", duplicate.Select(d => d.ProjectName ?? "<Unknown>"))}).");

        foreach (var anomaly in _matchService.AuditIndexConsistency())
            _logger.LogWarning($"[DIAG-554] match-cache audit: {anomaly}");
    }

    /// <summary>Compares a step's (document URI, line) coordinate for the duplicate check above; URIs compare case-insensitively, matching the rest of the document-keyed services.</summary>
    private sealed class StepLocationComparer : IEqualityComparer<(string Uri, int Line)>
    {
        public static readonly StepLocationComparer Instance = new();

        public bool Equals((string Uri, int Line) x, (string Uri, int Line) y) =>
            x.Line == y.Line && string.Equals(x.Uri, y.Uri, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Uri, int Line) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Uri ?? string.Empty), obj.Line);
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

    /// <summary>
    /// The non-actionable "nothing to navigate to" lens: same shape
    /// <see cref="BuildResolvedLens"/> produces for <c>count == 0</c>, but without needing a URI
    /// at all — deliberately so, since the callers reach this only when the URI is missing or
    /// unusable and must never hand the client a clickable command built from a fabricated one.
    /// </summary>
    private static global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens WithZeroUsages(
        global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens lens) =>
        new()
        {
            Range = lens.Range,
            Command = new Command { Title = "0 step usages", Name = "reqnroll.noStepUsages", Arguments = null }
        };

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFile(string a, string b) => PathUtils.IsSamePath(a, b);
}
