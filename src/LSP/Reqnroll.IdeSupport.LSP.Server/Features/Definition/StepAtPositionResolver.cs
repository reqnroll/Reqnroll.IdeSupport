#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Definition;

/// <summary>
/// Resolves the step, and the step-definition bindings it matched, at a caret position in a
/// <c>.feature</c> file. Shared by <see cref="DefinitionHandler"/> (<c>textDocument/definition</c>)
/// and <see cref="GoToStepDefinitionHandler"/> (<c>reqnroll/goToStepDefinition</c>, issue #757) so the
/// two can never disagree about which bindings a step has.
/// </summary>
internal sealed class StepAtPositionResolver
{
    private readonly IBindingMatchService      _matchService;
    private readonly IDocumentBufferService    _bufferService;
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger         _logger;
    private readonly string                    _logPrefix;

    /// <summary>Creates a resolver; <paramref name="logPrefix"/> names the calling handler in log lines.</summary>
    public StepAtPositionResolver(
        IBindingMatchService      matchService,
        IDocumentBufferService    bufferService,
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger         logger,
        string                    logPrefix)
    {
        _matchService  = matchService;
        _bufferService = bufferService;
        _scopeManager  = scopeManager;
        _logger        = logger;
        _logPrefix     = logPrefix;
    }

    /// <summary>
    /// Returns the step at <paramref name="position"/> in <paramref name="uri"/>, or
    /// <see langword="null"/> (with a verbose log line saying why) when there is none.
    /// </summary>
    public StepBindingMatch? FindStep(DocumentUri uri, Position position)
    {
        if (!uri.Path.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogVerbose($"{_logPrefix}: ignoring non-.feature URI {uri}");
            return null;
        }

        if (!_bufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"{_logPrefix}: no document buffer for {uri}");
            return null;
        }

        var snapshot = buffer.ToGherkinTextSnapshot();
        var offset   = snapshot.ToOffset(position.Line, position.Character);

        // Resolve the primary owner; fall back to Unknown for pre-baseline startup.
        var primaryOwner = _scopeManager.ResolvePrimaryOwner(uri);
        var owner = primaryOwner is not null
            ? new ProjectOwner(primaryOwner.ProjectFullName, primaryOwner.TargetFrameworkMoniker)
            : ProjectOwner.Unknown;

        if (!_matchService.TryGet(new MatchSetKey(uri.ToString(), owner), out var matchSet) || matchSet is null)
        {
            _logger.LogVerbose($"{_logPrefix}: no match set cached for {uri}");
            return null;
        }

        var step = matchSet.FindAt(offset);
        if (step is null)
            _logger.LogVerbose($"{_logPrefix}: no step at offset {offset} in {uri}");
        return step;
    }

    /// <summary>
    /// The bindings <paramref name="step"/> matched that record a source file, in match order.
    /// Bindings whose recorded file does not exist on this machine are included (callers decide
    /// how to present them) but each is logged once, since they have no navigation target here
    /// (issue #540).
    /// </summary>
    public IReadOnlyList<ProjectStepDefinitionBinding> GetBindingsWithSource(StepBindingMatch step)
    {
        var bindings = step.Result.Items
            .Select(item => item.MatchedStepDefinition)
            .Where(sd => sd?.Implementation?.SourceLocation?.SourceFile is not (null or ""))
            .Select(sd => sd!)
            .ToList();

        foreach (var impl in bindings.Select(sd => sd.Implementation).Where(i => i.SourceLocation!.IsResolved == false))
            _logger.LogInfo(
                $"{_logPrefix}: no local file for '{impl.Method}' — the compiled assembly records it at " +
                $"'{impl.SourceLocation!.RecordedSourceFile}', which does not exist on this machine. " +
                "Rebuild the project locally to restore navigation for this binding.");

        return bindings;
    }
}
