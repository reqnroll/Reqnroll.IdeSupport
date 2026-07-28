using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EnvDTE;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Reqnroll.IdeSupport.VisualStudio;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;

/// <summary>
/// Builds the JSON <c>params</c> payloads for <c>reqnroll/projectLoaded</c> and
/// <c>reqnroll/projectFiles</c> from a DTE <see cref="Project"/>.
/// </summary>
/// <remarks>
/// Extracted from <see cref="VsProjectEventMonitor"/> so the same payload-construction logic is
/// reusable by <see cref="LspProjectPreloadPusher"/>, which pushes the initial project state to
/// the server's <c>ProjectPreloadListener</c> side channel before the real LSP connection exists
/// (see docs/LSP-IDE-Support-Architecture.md's As-built note on eager server startup). Must be
/// called on the UI thread — all members read DTE/COM state.
/// </remarks>
internal static class VsProjectPayloadBuilder
{
    /// <summary>Builds the <c>reqnroll/projectLoaded</c> params JSON for a project (paths, TFM, NuGet package references). Must run on the UI thread.</summary>
    public static string BuildProjectLoadedParamsJson(
        Project project,
        string workspaceFolder,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var projectFile   = project.FullName;
        var projectFolder = Path.GetDirectoryName(projectFile) ?? string.Empty;

        var outputAssemblyPath = VsUtils.GetOutputAssemblyPath(project) ?? string.Empty;
        var tfm = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;

        var packageRefs = GetPackageReferences(project, serviceProvider, logger);

        var paramsObj = new
        {
            workspaceFolder,
            projectFile,
            projectFolder,
            outputAssemblyPath,
            targetFrameworkMoniker = tfm,
            packageReferences = packageRefs
        };

        return JsonConvert.SerializeObject(paramsObj, Formatting.None);
    }

    /// <summary>Builds the <c>reqnroll/projectFiles</c> baseline params JSON, listing every feature/binding file in the project. Must run on the UI thread.</summary>
    public static string BuildProjectFilesParamsJson(Project project, ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var tfm     = VsUtils.GetTargetFrameworkMoniker(project) ?? string.Empty;
        var entries = BuildProjectFileEntries(project, logger);

        var paramsObj = new
        {
            projectFile            = project.FullName,
            targetFrameworkMoniker = tfm,
            kind                   = 0,      // ProjectFilesKind.Baseline = 0
            files                  = entries,
        };

        return JsonConvert.SerializeObject(paramsObj, Formatting.None);
    }

    private static object[] BuildProjectFileEntries(Project project, ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var entries = new List<object>();
            // DTE can surface the same file on more than one path (e.g. a generated .feature.cs is
            // nested under its .feature via DependentUpon, causing the feature node to be walked
            // twice). Deduplicate by full path so the server receives each file once.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in VsUtils.GetPhysicalFileProjectItems(project))
            {
                var path = VsUtils.GetFilePath(item);
                if (path is null)
                    continue;

                int role;
                var ext = Path.GetExtension(path);
                if (ext.Equals(".feature", StringComparison.OrdinalIgnoreCase))
                    role = 0;   // ProjectFileRole.Feature = 0
                else if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    role = 1;   // ProjectFileRole.Binding = 1
                else
                    continue;

                if (!seen.Add(path))
                    continue;

                entries.Add(new { path, role, added = true });
            }
            return entries.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VsProjectPayloadBuilder: could not enumerate project items for {ProjectName}",
                project.Name);
            return Array.Empty<object>();
        }
    }

    private static object[] GetPackageReferences(Project project, IServiceProvider serviceProvider, ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetInstalledNuGetPackages(serviceProvider, project.FullName)
                .Select(p => (object)new
                {
                    packageId   = p.Id,
                    version     = p.Version,
                    installPath = p.InstallPath ?? string.Empty
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VsProjectPayloadBuilder: could not read NuGet packages for {ProjectName}",
                project.Name);
            return Array.Empty<object>();
        }
    }
}
