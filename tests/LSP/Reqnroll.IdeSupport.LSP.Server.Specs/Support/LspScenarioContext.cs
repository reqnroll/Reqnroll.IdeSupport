using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.LSP.Server.Features.Definition;
using Reqnroll.IdeSupport.LSP.Server.Features.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.LSP.Server.Features.References;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.Support;

/// <summary>
/// Per-scenario state shared between step classes via Reqnroll's container.
/// Owns the <see cref="LspServerHarness"/> and a temporary workspace folder.
/// <para>
/// The workspace folder is the <em>solution</em> root, not a project folder. A scenario that
/// only ever needs one project lets it default to <see cref="DefaultProjectFileName"/> rooted at
/// the workspace folder itself; a scenario exercising project membership registers two or more
/// projects in sub-folders (see <see cref="RegisterProject"/>), which additionally makes the
/// workspace root a location that lies outside every project folder — the "owned by no project"
/// case the membership index has to handle.
/// </para>
/// </summary>
public sealed class LspScenarioContext
{
    /// <summary>The project every scenario gets when it never names one explicitly.</summary>
    public const string DefaultProjectFileName = "Sample.csproj";

    /// <summary>The TFM announced for a project whose steps do not specify one.</summary>
    public const string DefaultTargetFrameworkMoniker = ".NETCoreApp,Version=v8.0";

    private readonly Dictionary<string, SpecProject> _projects = new(StringComparer.OrdinalIgnoreCase);

    public LspScenarioContext()
    {
        WorkspaceFolder = Path.Combine(Path.GetTempPath(), "ReqnrollLspSpecs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceFolder);
    }

    public LspServerHarness Harness { get; } = new();
    public string WorkspaceFolder { get; }
    public bool Started { get; set; }

    // Tracking for the most recently opened document, used by Then-steps.
    public DocumentUri? LastUri { get; set; }
    public string LastDocumentText { get; set; } = string.Empty;
    public int LastVersion { get; set; }
    public SemanticTokens? LastTokens { get; set; }
    public LocationOrLocationLinks? LastReferences { get; set; }
    public FindStepUsagesResponse? LastFindStepUsages { get; set; }
    public GoToHooksResponse? LastGoToHooks { get; set; }
    public CodeLens[]? LastCodeLens { get; set; }
    public CompletionList? LastCompletions { get; set; }
    public TextEdit[]? LastFormattingEdits { get; set; }
    public SymbolInformationOrDocumentSymbolContainer? LastDocumentSymbols { get; set; }
    public Container<FoldingRange>? LastFoldingRanges { get; set; }
    public ApplyWorkspaceEditParams? LastToggleEdit { get; set; }

    // F15 — Find Unused Step Definitions
    public FindUnusedStepDefinitionsResponse? LastFindUnused { get; set; }

    // F16 — Step Rename
    public WorkspaceEdit? LastRenameEdit { get; set; }
    public RenameTargetsResponse? LastRenameTargets { get; set; }
    public OmniSharp.Extensions.LanguageServer.Protocol.Models.RangeOrPlaceholderRange? LastPrepareRenameRange { get; set; }

    // F6 — Define Steps (code actions)
    public CommandOrCodeActionContainer? LastCodeActions { get; set; }

    /// <summary>
    /// Resolves a workspace-relative path to a full path. Forward slashes are accepted (and
    /// preferred) in feature files so a scenario reads the same on every platform; they are
    /// normalised to the platform separator here, because the server compares membership-index
    /// paths against the paths carried on <c>textDocument/*</c> URIs.
    /// </summary>
    public string PathFor(string relativePath)
        => Path.GetFullPath(Path.Combine(
            WorkspaceFolder,
            relativePath.Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar)));

    public DocumentUri UriFor(string relativeName)
        => DocumentUri.FromFileSystemPath(PathFor(relativeName));

    // ── Projects ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a project so later steps can announce files for it, resolve its folder, and
    /// unload it by name. Registering the same project file twice updates it rather than adding
    /// a second entry, so a scenario may re-announce a project without duplicating it.
    /// </summary>
    /// <param name="projectFileName">
    /// The project file, workspace-relative (e.g. <c>"Linking/Linking.csproj"</c>) or a bare file
    /// name (e.g. <c>"Sample.csproj"</c>). The bare file name is also the key later steps use.
    /// </param>
    /// <param name="projectFolder">
    /// The project's own folder, workspace-relative. Null or empty means the workspace root —
    /// the single-project layout every pre-existing scenario uses.
    /// </param>
    /// <param name="targetFrameworkMoniker">Defaults to <see cref="DefaultTargetFrameworkMoniker"/>.</param>
    public SpecProject RegisterProject(
        string projectFileName,
        string? projectFolder = null,
        string? targetFrameworkMoniker = null)
    {
        var project = new SpecProject(
            ProjectFile: PathFor(projectFileName),
            ProjectFolder: string.IsNullOrEmpty(projectFolder) ? WorkspaceFolder : PathFor(projectFolder),
            TargetFrameworkMoniker: targetFrameworkMoniker ?? DefaultTargetFrameworkMoniker);

        // Keyed on the full project-file path, not the bare file name: two projects in different
        // folders may legitimately share a file name, and a name-keyed registry would silently
        // point the second one's steps at the first one's folder.
        _projects[project.ProjectFile] = project;
        return project;
    }

    /// <summary>
    /// Looks up a project by the same string a step used to name it — either the workspace-relative
    /// path it was registered with, or a bare file name when that name identifies exactly one
    /// registered project. A project a scenario never registered resolves to one rooted at the
    /// workspace folder, which is what keeps single-project scenarios — where
    /// <c>reqnroll/projectFiles</c> names a project no explicit step ever created — working
    /// unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A bare file name matches more than one registered project. Silently picking one would
    /// attribute files to the wrong project, so the scenario is told to name the project by its
    /// workspace-relative path instead.
    /// </exception>
    public SpecProject GetProject(string projectFileName)
    {
        if (_projects.TryGetValue(PathFor(projectFileName), out var exact))
            return exact;

        var fileName = Path.GetFileName(projectFileName);
        var byName = _projects.Values
            .Where(p => string.Equals(Path.GetFileName(p.ProjectFile), fileName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (byName.Count > 1)
            throw new InvalidOperationException(
                $"'{projectFileName}' matches {byName.Count} registered projects " +
                $"({string.Join(", ", byName.Select(p => p.ProjectFile))}). " +
                "Name the project by its workspace-relative path so the step is unambiguous.");

        return byName.Count == 1
            ? byName[0]
            : new SpecProject(PathFor(projectFileName), WorkspaceFolder, DefaultTargetFrameworkMoniker);
    }

    /// <summary>A project as the spec harness announces it over <c>reqnroll/projectLoaded</c>.</summary>
    public sealed record SpecProject(string ProjectFile, string ProjectFolder, string TargetFrameworkMoniker);

    public async Task EnsureStartedAsync(string? ideId = null, bool supportsChangeAnnotations = false)
    {
        if (Started) return;
        await Harness.StartAsync(WorkspaceFolder, ideId, supportsChangeAnnotations).ConfigureAwait(false);
        Started = true;
    }

    public async Task DisposeAsync()
    {
        await Harness.DisposeAsync().ConfigureAwait(false);
        try { if (Directory.Exists(WorkspaceFolder)) Directory.Delete(WorkspaceFolder, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
