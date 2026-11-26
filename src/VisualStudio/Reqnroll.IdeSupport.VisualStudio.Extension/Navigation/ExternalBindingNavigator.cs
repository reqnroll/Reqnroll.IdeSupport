#nullable enable

using System.IO;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Reqnroll.IdeSupport.Common.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Navigation;

/// <summary>
/// Opens a binding that has no source file on this machine (for example one in a referenced
/// package) through Visual Studio's own symbol navigation, which uses Source Link, embedded
/// source or decompilation according to the user's settings.
/// </summary>
/// <remarks>
/// The binding's <see cref="BindingMethodIdentity"/> is resolved in the compilation of the project
/// that owns the <c>.feature</c> file, since that project references the binding assembly. A
/// multi-targeted project has one Roslyn project per target framework; each is tried in turn and the
/// first that resolves the method is used, so the navigation does not depend on which framework is
/// active.
/// </remarks>
internal static class ExternalBindingNavigator
{
    /// <summary>
    /// Navigates to the method <paramref name="identity"/> names, resolved from the project that owns
    /// <paramref name="originatingFilePath"/>. Returns <see langword="false"/> (and logs why) when the
    /// project, the method or the navigation is unavailable.
    /// </summary>
    public static async Task<bool> NavigateAsync(
        BindingMethodIdentity identity,
        string                originatingFilePath,
        IIdeSupportLogger     logger,
        CancellationToken     cancellationToken)
    {
        try
        {
            return await NavigateCoreAsync(identity, originatingFilePath, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roslyn and the editor's navigation service are outside our control; a failure there is a
            // binding that could not be opened, reported as such, not a fault in the command.
            logger.LogWarning($"ExternalBindingNavigator: navigating to '{identity.DocumentationCommentId}' failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> NavigateCoreAsync(
        BindingMethodIdentity identity,
        string                originatingFilePath,
        IIdeSupportLogger     logger,
        CancellationToken     cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var componentModel = ServiceProvider.GlobalProvider.GetService(typeof(SComponentModel)) as IComponentModel;
        var workspace = componentModel?.GetService<VisualStudioWorkspace>();
        if (workspace is null)
        {
            logger.LogWarning("ExternalBindingNavigator: the Roslyn workspace is not available.");
            return false;
        }

        var projectFile = GetContainingProjectFile(originatingFilePath);
        if (projectFile is null)
        {
            logger.LogWarning($"ExternalBindingNavigator: no project contains '{originatingFilePath}'.");
            return false;
        }

        var candidates = workspace.CurrentSolution.Projects
            .Where(p => p.FilePath is not null &&
                        string.Equals(Path.GetFullPath(p.FilePath), projectFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
        {
            logger.LogWarning($"ExternalBindingNavigator: the Roslyn workspace has no project for '{projectFile}'.");
            return false;
        }

        // Building a compilation can take a while in a large project: keep it off the UI thread.
        await TaskScheduler.Default;
        var (symbol, project) = await ResolveAsync(identity, candidates, cancellationToken).ConfigureAwait(false);
        if (symbol is null || project is null)
        {
            logger.LogWarning(
                $"ExternalBindingNavigator: no method '{identity.DocumentationCommentId}' in '{identity.AssemblyIdentity}' " +
                $"is referenced by '{projectFile}'. Rebuild the project if the binding assembly has changed.");
            return false;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var navigated = await workspace.TryGoToDefinitionAsync(symbol, project, cancellationToken);
        logger.LogInfo(
            $"ExternalBindingNavigator: {(navigated ? "navigated to" : "Visual Studio could not open")} " +
            $"'{identity.DocumentationCommentId}' via '{project.Name}'.");
        return navigated;
    }

    private static async Task<(IMethodSymbol? Symbol, Project? Project)> ResolveAsync(
        BindingMethodIdentity identity, IReadOnlyList<Project> candidates, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await candidate.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var symbol = compilation is null ? null : BindingSymbolResolver.Resolve(compilation, identity);
            if (symbol is not null)
                return (symbol, candidate);
        }

        return (null, null);
    }

    private static string? GetContainingProjectFile(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = ServiceProvider.GlobalProvider.GetService(typeof(EnvDTE.DTE)) as DTE2;
        var projectFile = dte?.Solution?.FindProjectItem(filePath)?.ContainingProject?.FullName;
        return string.IsNullOrEmpty(projectFile) ? null : Path.GetFullPath(projectFile);
    }
}
