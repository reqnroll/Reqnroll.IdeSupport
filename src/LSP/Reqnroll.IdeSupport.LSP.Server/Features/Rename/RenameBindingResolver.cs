using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Resolves the step-definition binding(s) at a cursor position for the rename feature.
/// Extracted from <see cref="StepRenameHandler"/> (issue #139) — "resolve binding at cursor" was
/// duplicated in spirit across <c>HandlePrepareRenameAsync</c>, <c>HandleRenameAsync</c>, and the
/// <c>reqnroll/renameTargets</c> handlers; this centralises the shared primitives.
/// </summary>
internal sealed class RenameBindingResolver
{
    private readonly IBindingMatchService      _matchService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly RenameSessionManager      _sessionManager;
    private readonly IIdeSupportLogger         _logger;

    public RenameBindingResolver(
        IBindingMatchService      matchService,
        ILspWorkspaceScopeManager scopeManager,
        RenameSessionManager      sessionManager,
        IIdeSupportLogger         logger)
    {
        _matchService    = matchService;
        _scopeManager    = scopeManager;
        _sessionManager  = sessionManager;
        _logger          = logger;
    }

    /// <summary>
    /// Finds all bindings that match the feature step at the given cursor position
    /// by querying the binding match cache for the owning projects.
    /// </summary>
    public List<ProjectStepDefinitionBinding> FindBindingsAtFeatureStep(
        DocumentUri uri, string path, Position position) =>
        FindBindingsAtFeatureStep(uri, path, position, out _);

    /// <summary>
    /// Finds all bindings that match the feature step at the given cursor position, and the
    /// matched step's own text span (excluding the keyword/indentation) via <paramref
    /// name="matchedRange"/>. Callers that only need the range for editing (prepareRename must
    /// offer exactly the text that HandleRenameAsync will later replace at <c>usage.Range</c> —
    /// otherwise the keyword/indentation the client seeds the dialog with gets duplicated when
    /// the edit is applied) should use this overload.
    /// </summary>
    public List<ProjectStepDefinitionBinding> FindBindingsAtFeatureStep(
        DocumentUri uri, string path, Position position, out LspRange? matchedRange)
    {
        matchedRange = null;

        var uriStr = uri.ToString();
        var owners = _scopeManager.ResolveOwners(uri);
        if (owners.Count == 0)
            return new List<ProjectStepDefinitionBinding>();

        var matchedBindings = new HashSet<ProjectStepDefinitionBinding>();

        foreach (var owner in owners)
        {
            var key = new MatchSetKey(uriStr, new ProjectOwner(owner.ProjectFullName, owner.TargetFrameworkMoniker));
            if (!_matchService.TryGet(key, out var matchSet))
                continue;

            foreach (var step in matchSet.Steps)
            {
                // Ambiguous steps (2+ matching bindings) are exactly what the rename-targets
                // picker exists to disambiguate — MatchResult.HasDefined is false for them
                // (their items are typed Ambiguous, not Defined), so it must not gate them out
                // here alongside genuinely undefined steps.
                if (step.Result is null || !(step.Result.HasDefined || step.Result.HasAmbiguous))
                    continue;

                // Tolerate a cursor anywhere on the step's line(s), not just within the exact
                // step-text span (e.g. on the keyword or leading indentation) — this used to
                // compare position.Character directly against step.Range's own start/end
                // character, the same narrow exact-text-span bug fixed for Go to Definition,
                // just re-derived independently here rather than shared.
                var snapshot  = step.Range.Snapshot;
                var startLine = snapshot.GetLineFromLineNumber(step.Range.StartLinePosition.Line);
                var endLine   = snapshot.GetLineFromLineNumber(step.Range.EndLinePosition.Line);
                var offset    = snapshot.ToOffset(position.Line, position.Character);
                if (offset >= startLine.Start && offset <= endLine.End)
                {
                    matchedRange ??= step.Range.ToLspRange();
                    foreach (var item in step.Result.Items)
                    {
                        if (item.MatchedStepDefinition != null)
                            matchedBindings.Add(item.MatchedStepDefinition);
                    }
                }
            }
        }

        return matchedBindings.ToList();
    }

    /// <summary>
    /// Finds all bindings whose attributes are declared on the same C# method as the given
    /// 1-based <paramref name="line"/> in <paramref name="path"/> — the genuine "multi-attribute"
    /// case ([Given]/[When]/[Then] stacked on one method). Via the registry's own step-definition
    /// list rather than the match cache (used for .cs-side lookups, where there is no
    /// feature-step match set to query).
    /// </summary>
    /// <remarks>
    /// A naive "within N lines of any binding" window incorrectly merges two *different*,
    /// closely-spaced methods into one false-ambiguous picker (#170 regression: two step methods
    /// 6 lines apart, cursor on the second one's attribute, both fell within a 5-line window of
    /// the first). Instead this mirrors <see cref="ProjectBindingRegistry"/>'s own
    /// <c>CoversQuery</c> exact-match logic: first find the single binding the cursor actually
    /// covers (its own attribute line, or the method-identifier line, for syntax-discovered
    /// bindings that carry <see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/>; a
    /// heuristic window only for connector-discovered bindings that lack it), then return every
    /// binding sharing that exact method-identifier line — which is precisely the set of
    /// attributes stacked on that one method, no matter how close another, different method
    /// happens to sit in the file.
    /// </remarks>
    /// <param name="requireAttributeLine">
    /// When <see langword="true"/> (issue #506 — VS Code's F2 dispatcher), only the binding's own
    /// attribute line counts as a match: the method-identifier-line fallback and the
    /// connector-discovered heuristic window are both disabled, so a cursor on the method name or
    /// its body no longer counts as "on the binding" and F2 falls through to the native C# rename
    /// instead of hijacking it. Explicit invocations (context menu, command palette) leave this
    /// <see langword="false"/> to keep the original, deliberately loose behavior — a user who
    /// picked "Reqnroll: Rename Step" from anywhere on the method has clearly stated their intent.
    /// </param>
    public static List<ProjectStepDefinitionBinding> FindBindingsAtCSharpMethod(
        ProjectBindingRegistry registry, string path, int line, bool requireAttributeLine = false)
    {
        var candidates = registry.StepDefinitions
            .Where(b => b.Implementation.SourceLocation != null &&
                        string.Equals(b.Implementation.SourceLocation.SourceFile, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var anchor = candidates.FirstOrDefault(b => CoversLine(b, line, requireAttributeLine));
        if (anchor == null)
            return new List<ProjectStepDefinitionBinding>();

        var anchorMethodLine = anchor.Implementation.SourceLocation!.SourceFileLine;
        return candidates
            .Where(b => b.Implementation.SourceLocation!.SourceFileLine == anchorMethodLine)
            .ToList();
    }

    // Mirrors ProjectBindingRegistry.CoversQuery (kept in sync manually — see its own comment):
    // exact attribute/method-line match for syntax-discovered bindings, a heuristic window only
    // as a fallback for connector-discovered bindings that lack AttributeSourceLine.
    private static bool CoversLine(ProjectStepDefinitionBinding binding, int line, bool requireAttributeLine)
    {
        var sourceFileLine = binding.Implementation.SourceLocation!.SourceFileLine;
        if (binding.AttributeSourceLine.HasValue)
            return requireAttributeLine
                ? line == binding.AttributeSourceLine.Value
                : line == binding.AttributeSourceLine.Value || line == sourceFileLine;

        // No precise attribute location is known for a connector-discovered binding — a strict
        // caller must not guess via the heuristic window below.
        if (requireAttributeLine)
            return false;

        const int attributeLeeway = 5;
        return Math.Abs(sourceFileLine - line) <= attributeLeeway;
    }

    /// <summary>
    /// Resolves the binding to rename for <c>textDocument/rename</c>: honors a pending
    /// <c>reqnroll/selectRenameTarget</c> session first (the multi-attribute picker flow), then
    /// falls back to feature-match-cache position lookup (<c>.feature</c>) or
    /// <see cref="ProjectBindingRegistry.FindBindingAtLocation"/> (<c>.cs</c>, or no session
    /// match). Returns <see langword="null"/> when no binding can be resolved at all.
    /// </summary>
    public ProjectStepDefinitionBinding? ResolveBindingForRename(
        DocumentUri uri, string path, Position position, ProjectBindingRegistry registry)
    {
        var line   = position.Line + 1;
        var column = position.Character + 1;

        // Check for a pending rename session (set by reqnroll/selectRenameTarget).
        // This handles the multi-attribute case where the cursor is not on a specific
        // attribute string — the picker pre-selected which binding to rename.
        ProjectStepDefinitionBinding? binding = null;

        // Use version from request or fallback to 0
        var documentVersion = 0;
        if (_sessionManager.TryConsume(uri.ToString(), documentVersion, out var sessionAttrIndex))
        {
            _logger.LogVerbose($"RenameBindingResolver: consumed pending session, attributeIndex={sessionAttrIndex}");

            var bindingsAtLocation = path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase)
                ? FindBindingsAtFeatureStep(uri, path, position)
                : FindBindingsAtCSharpMethod(registry, path, line);

            if (sessionAttrIndex >= 0 && sessionAttrIndex < bindingsAtLocation.Count)
            {
                binding = bindingsAtLocation[sessionAttrIndex];
                _logger.LogVerbose($"RenameBindingResolver: resolved binding via session: '{binding?.Expression}'");
            }
        }

        // Fall back to position-based resolution (single-binding case)
        if (binding == null && path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            var featureBindings = FindBindingsAtFeatureStep(uri, path, position);
            binding = featureBindings.FirstOrDefault();
            if (binding != null)
                _logger.LogVerbose($"RenameBindingResolver: resolved binding via feature match cache: '{binding.Expression}'");
        }

        binding ??= registry.FindBindingAtLocation(new SourceLocation(path, line, column));
        if (binding == null)
            _logger.LogVerbose("RenameBindingResolver: no binding at cursor position");

        return binding;
    }
}
