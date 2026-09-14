using OmniSharp.Extensions.LanguageServer.Protocol;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeActions;

/// <summary>
/// Where a "Define step(s)" code action's generated code should go: the snippet style, the
/// class/namespace/target-path a brand-new file would use, and the existing binding files
/// ranked as append candidates instead.
/// </summary>
internal sealed record StepDefinitionTarget(
    SnippetExpressionStyle Style,
    CSharpCodeGenerationConfiguration CSharpConfig,
    string ClassName,
    string Namespace,
    string TargetPath,
    IReadOnlyList<string> AppendCandidates,
    string Indent,
    string NewLine);

/// <summary>
/// Resolves a <see cref="StepDefinitionTarget"/> for a "Define Steps" code action (issue #588).
/// Extracted from <see cref="CodeActionHandler.Handle"/>, which combined this resolution with
/// action-building and request-orchestration in one 179-line method — this class owns only
/// "where should the generated code go", independent of what steps end up in it.
/// </summary>
internal sealed class StepDefinitionTargetResolver
{
    /// <summary>
    /// Caps how many existing binding files are offered as append targets for one "Define
    /// step(s)" title, so the lightbulb menu doesn't grow unbounded on a project with many
    /// binding files matched to a feature.
    /// </summary>
    internal const int MaxAppendCandidates = 5;

    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IFileSystemForIDE _fileSystem;

    /// <summary>Initializes a new instance of the <see cref="StepDefinitionTargetResolver"/> class.</summary>
    public StepDefinitionTargetResolver(ILspWorkspaceScopeManager scopeManager, IFileSystemForIDE fileSystem)
    {
        _scopeManager = scopeManager;
        _fileSystem = fileSystem;
    }

    /// <summary>Resolves the target for a "Define Steps" action on <paramref name="featurePath"/>.</summary>
    public StepDefinitionTarget Resolve(
        DocumentUri uri,
        string featurePath,
        LspReqnrollProject? primaryOwner,
        FeatureBindingMatchSet? matchSet)
    {
        // Read skeleton style from project config.
        var configProvider = _scopeManager.GetConfigurationProviderForUri(uri);
        var config = configProvider.GetConfiguration();
        var style  = config?.SnippetExpressionStyle ?? SnippetExpressionStyle.CucumberExpression;
        var csharpConfig = new CSharpCodeGenerationConfiguration();

        // Determine target file metadata.
        var className     = StepDefinitionFileBuilder.ClassNameFromFeaturePath(featurePath);
        var defaultNs     = primaryOwner?.DefaultNamespace ?? Path.GetFileNameWithoutExtension(featurePath);
        var projectFolder = primaryOwner?.ProjectFolder ?? Path.GetDirectoryName(featurePath) ?? string.Empty;
        var bindingPaths  = primaryOwner is not null
            ? _scopeManager.GetBindingFilePathsForProject(primaryOwner)
            : (IReadOnlyCollection<string>)Array.Empty<string>();

        // Rank existing binding files by how many of *this feature's* steps are already matched
        // there — a stronger placement signal than "which folder has the most binding files
        // anywhere in the project" (used only as the fallback below). Capped so the lightbulb
        // menu doesn't grow unbounded.
        var appendCandidates = (matchSet is not null
                ? CandidateStepDefinitionFileRanker.RankCandidateFiles(matchSet)
                : Array.Empty<string>())
            .Where(f => _fileSystem.File.Exists(f))
            .Take(MaxAppendCandidates)
            .ToList();

        // The new-file fallback's folder: alongside the top-ranked candidate (even when that
        // specific file is later declined for append), or the project-wide folder-frequency
        // heuristic only when the feature has no ranked candidates at all (e.g. a brand-new
        // feature with no bindings anywhere yet).
        var newFileFolder = appendCandidates.Count > 0
            ? Path.GetDirectoryName(appendCandidates[0]) is { Length: > 0 } dir ? dir : projectFolder
            : FindBestTargetFolder(_fileSystem, bindingPaths, featurePath);

        var targetPath = Path.Combine(newFileFolder, className + ".cs");
        if (_fileSystem.File.Exists(targetPath))
        {
            int suffix = 2;
            while (_fileSystem.File.Exists(Path.Combine(newFileFolder, className + suffix + ".cs")))
                suffix++;
            targetPath = Path.Combine(newFileFolder, className + suffix + ".cs");
        }
        className = Path.GetFileNameWithoutExtension(targetPath);
        var @namespace = StepDefinitionFileBuilder.DeriveNamespace(projectFolder, defaultNs, targetPath);

        return new StepDefinitionTarget(
            style, csharpConfig, className, @namespace, targetPath, appendCandidates, "    ", Environment.NewLine);
    }

    /// <summary>
    /// Picks the best target directory for a new step-definition file.
    /// Prefers the folder that already holds the most binding files (so the generated file
    /// lands alongside the user's existing step definitions), then falls back to a sibling
    /// StepDefinitions/ folder or the feature file's own directory.
    /// </summary>
    private static string FindBestTargetFolder(
        IFileSystemForIDE fileSystem,
        IReadOnlyCollection<string> bindingFiles,
        string featureFilePath)
    {
        if (bindingFiles.Count > 0)
        {
            var best = bindingFiles
                .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
                .Where(d => d.Length > 0)
                .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (best is not null)
                return best.Key;
        }

        var featureDir    = Path.GetDirectoryName(featureFilePath) ?? string.Empty;
        var siblingStepDefs = Path.Combine(featureDir, "StepDefinitions");
        return fileSystem.Directory.Exists(siblingStepDefs) ? siblingStepDefs : featureDir;
    }
}
