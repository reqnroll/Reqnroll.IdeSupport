using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Handles the standard <c>textDocument/codeLens</c> request for hook-binding methods in `.cs`
/// files (hook-match-count CodeLens — issue #373). Returns one lens per hook-binding attribute
/// showing how many project scenarios its scope matches (e.g. "3 scenarios matched"), the inverse
/// of #269's hook-match CodeLens on `.feature` lines. Clicking a lens invokes the client's
/// <c>reqnroll.goToMatchingScenarios</c> command at that attribute's exact position.
/// </summary>
/// <remarks>
/// <para>
/// Unlike #269's `.cs`-vs-`.feature` split (where each handler owns a whole file type
/// exclusively), this handler must **coexist** with <see cref="StepCodeLensHandler"/> on the same
/// `.cs` file: a single `[Binding]` class routinely mixes step-binding and hook-binding methods.
/// Both handlers are combined in the same <c>textDocument/codeLens</c> response — see the
/// registration in <c>LanguageServerOptionsExtensions</c> — filtering by which registry
/// collection (<see cref="ProjectBindingRegistry.Hooks"/> vs <c>StepDefinitions</c>) a binding's
/// source file matches, not by file extension.
/// </para>
/// <para>
/// <see cref="HookType.BeforeTestRun"/>/<see cref="HookType.AfterTestRun"/> hooks are skipped
/// entirely (no lens) — see <see cref="HookScenarioMatching.IsScenarioCountable"/>. A hook with
/// zero matching scenarios still renders ("0 scenarios matched") rather than being suppressed,
/// deliberately diverging from #269's "skip empty" convention: a hook matching nothing is likely
/// a bug (dead code, a typo'd tag expression), so it's the most actionable case, not the least.
/// </para>
/// <para>
/// An unscoped hook (no <c>[Scope]</c> at all) matches every scenario in the project — a count
/// here would be technically correct but unbounded and uninformative, so the lens shows the
/// static label "all scenarios" instead and skips the corpus walk entirely (issue #403). The
/// click action is unaffected: <c>reqnroll/goToMatchingScenarios</c> still resolves and returns
/// the full scenario list on demand.
/// </para>
/// </remarks>
public sealed class HookMatchCountCodeLensHandler
{
    private readonly IBindingMatchService          _matchService;
    private readonly ILspWorkspaceScopeManager     _scopeManager;
    private readonly IProjectBindingRegistryLookup _registryLookup;
    private readonly IIdeSupportLogger               _logger;
    private readonly IOperationDurationRecorder    _recorder;

    /// <summary>Initializes a new instance of the <see cref="HookMatchCountCodeLensHandler"/> class.</summary>
    public HookMatchCountCodeLensHandler(
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
    /// Returns one lens per scenario-countable hook-binding attribute in the requested `.cs` file.
    /// Returns an empty array for non-.cs files or when the file has no discovered hook bindings.
    /// </summary>
    public Task<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[]> HandleAsync(
        CodeLensParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;

        using var _perf = _recorder.Measure(LspMethodNames.TextDocumentCodeLens, uri);

        if (!IsCSharp(uri))
        {
            _logger.LogVerbose($"HookMatchCountCodeLensHandler: ignoring non-.cs URI {uri}");
            return Task.FromResult(Empty);
        }

        var filePath = uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath))
            return Task.FromResult(Empty);

        var registry = _registryLookup.GetRegistryForUri(uri);
        if (registry == ProjectBindingRegistry.Invalid || registry.Hooks.Length == 0)
        {
            _logger.LogVerbose($"HookMatchCountCodeLensHandler: no registry or no hooks for {uri}");
            return Task.FromResult(Empty);
        }

        var owners = _scopeManager.ResolveOwners(uri);
        IReadOnlyCollection<ProjectOwner>? projectFilter = owners.Count > 0
            ? owners.Select(p => new ProjectOwner(p.ProjectFullName, p.TargetFrameworkMoniker))
                    .ToArray()
            : null;

        // Computed once per request, not once per hook: HookScenarioMatching walks the full
        // project scenario corpus, and a "Hooks.cs" file can have many hook methods.
        var matchSets = _matchService.GetAll(projectFilter).ToList();

        var lenses = new List<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();
        var seen = new HashSet<(int line, int col)>();

        foreach (var hook in registry.Hooks)
        {
            if (!hook.IsValid) continue;
            if (!HookScenarioMatching.IsScenarioCountable(hook.HookType)) continue;

            var src = hook.Implementation?.SourceLocation;
            if (src is null || string.IsNullOrEmpty(src.SourceFile)) continue;
            if (!IsSameFile(src.SourceFile, filePath)) continue;

            var attrKey = (src.SourceFileLine, src.SourceFileColumn);
            if (!seen.Add(attrKey)) continue;

            // Unscoped hooks (no [Scope] at all) match every scenario in the project: skip the
            // corpus walk and show a static label rather than an unbounded, uninformative count
            // (issue #403).
            string title;
            if (hook.Scope is null)
            {
                title = "all scenarios";
            }
            else
            {
                var scenarios = HookScenarioMatching.ResolveMatchingScenarios(matchSets, hook);
                var count = scenarios.Count;
                title = count == 1 ? "1 scenario matched" : $"{count} scenarios matched";
            }

            // LSP positions are 0-based; SourceFileLine/SourceFileColumn are 1-based.
            var line = src.SourceFileLine   - 1;
            var col  = src.SourceFileColumn - 1;

            lenses.Add(new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
            {
                Range = new LspRange(new Position(line, col), new Position(line, col)),
                Command = new Command
                {
                    Title     = title,
                    Name      = "reqnroll.goToMatchingScenarios",
                    Arguments = new JArray(uri.ToString(), line, col),
                },
            });
        }

        _logger.LogVerbose($"HookMatchCountCodeLensHandler: {lenses.Count} lens(es) for {uri}");
        return Task.FromResult(lenses.ToArray());
    }

    private static readonly global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens[] Empty =
        Array.Empty<global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens>();

    private static bool IsCSharp(DocumentUri uri) =>
        uri.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameFile(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a),
            Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);
}
