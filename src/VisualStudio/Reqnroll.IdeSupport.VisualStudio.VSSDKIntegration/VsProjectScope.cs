#nullable disable

using EnvDTE;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.ProjectSystem.Settings;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using System.Collections.Concurrent;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Visual Studio's <see cref="IProjectScope"/> implementation, backed by an EnvDTE
/// <see cref="Project"/>: exposes project metadata (folder, output path, target frameworks,
/// namespace, NuGet package references) and feature-file discovery to the rest of the
/// integration.
/// </summary>
public class VsProjectScope : IProjectScope
{
    private readonly Project _project;

    /// <summary>Creates a project scope for <paramref name="project"/>, resolving its folder and name eagerly.</summary>
    public VsProjectScope(string id, Project project, IIdeScope ideScope)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _project = project;
        IdeScope = ideScope;
        ProjectFolder = VsUtils.GetProjectFolder(project);
        ProjectName = project.Name;
        ProjectFullName = project.FullName;
        Debug.Assert(ProjectFolder != null, "VsxHelper.IsSolutionProject ensures a not-null ProjectFolder");
    }

    private IIdeSupportLogger Logger => IdeScope.Logger;
    private ITelemetryService TelemetryService => IdeScope.TelemetryService;
    /// <summary>Arbitrary per-type extension properties attached to this project scope; disposed with it.</summary>
    public ConcurrentDictionary<Type, object> Properties { get; } = new();
    /// <summary>The project's root folder on disk.</summary>
    public string ProjectFolder { get; }
    /// <summary>The project's build output assembly path.</summary>
    public string OutputAssemblyPath { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetOutputAssemblyPath(_project); } }
    /// <summary>The project's single target framework moniker.</summary>
    public string TargetFrameworkMoniker { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetTargetFrameworkMoniker(_project); } }
    /// <summary>The project's target framework monikers (multi-targeting aware).</summary>
    public string TargetFrameworkMonikers { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetTargetFrameworkMonikers(_project); } }
    /// <summary>The project's platform target name (falls back to the platform name if unset).</summary>
    public string PlatformTargetName { get { ThreadHelper.ThrowIfNotOnUIThread(); return VsUtils.GetPlatformTargetName(_project) ?? VsUtils.GetPlatformName(_project); } }
    /// <summary>The project's display name.</summary>
    public string ProjectName { get; }
    /// <summary>The project's full file path.</summary>
    public string ProjectFullName { get; }
    /// <summary>The project's default namespace, or <see langword="null"/> if it could not be resolved.</summary>
    public string DefaultNamespace { get { ThreadHelper.ThrowIfNotOnUIThread(); return GetDefaultNamespace(); } }

    /// <summary>The IDE scope that owns this project scope.</summary>
    public IIdeScope IdeScope { get; }
    /// <summary>The NuGet package references installed in this project.</summary>
    public IEnumerable<NuGetPackageReference> PackageReferences { get { ThreadHelper.ThrowIfNotOnUIThread(); return GetPackageReferences(); } }

    //public void AddFile(string targetFilePath, string template)
    //{
    //    //TODO: handle template parameters
    //    IdeScope.FileSystem.File.WriteAllText(targetFilePath, template);
    //    _project.ProjectItems.AddFromFile(targetFilePath);
    //}

    /// <summary>Counts the project's <c>.feature</c> files, or returns <see langword="null"/> if enumeration fails.</summary>
    public int? GetFeatureFileCount()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetPhysicalFileProjectItems(_project)
                .Count(pi => FileSystemHelper.IsOfType(VsUtils.GetFilePath(pi), ".feature"));
        }
        catch (Exception e)
        {
            Logger.LogVerboseException(TelemetryService, e);
            return null;
        }
    }

    /// <summary>Returns the full paths of the project's physical files matching <paramref name="extension"/>, or an empty array on failure.</summary>
    public string[] GetProjectFiles(string extension)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetPhysicalFileProjectItems(_project)
                .Select(VsUtils.GetFilePath)
                .Where(fp => FileSystemHelper.IsOfType(fp, extension))
                .ToArray();
        }
        catch (Exception e)
        {
            Logger.LogVerboseException(TelemetryService, e);
            return new string[0];
        }
    }

    /// <summary>Disposes any <see cref="IDisposable"/> values stored in <see cref="Properties"/>.</summary>
    public void Dispose()
    {
        foreach (var disposableProperty in Properties.Values.OfType<IDisposable>())
            disposableProperty.Dispose();
    }

    private string GetDefaultNamespace()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return _project.Properties.Item("DefaultNamespace")?.Value as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private NuGetPackageReference[] GetPackageReferences()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return VsUtils.GetInstalledNuGetPackages((IdeScope as VsIdeScope).ServiceProvider, _project.FullName)
                .Select(pmd =>
                    new NuGetPackageReference(pmd.Id, new NuGetVersion(pmd.Version, pmd.RequestedRange),
                        pmd.InstallPath))
                .ToArray();
        }
        catch (Exception e)
        {
            if (IdeScope.IsSolutionLoaded)
                Logger.LogVerboseException(TelemetryService, e);
            else
                Logger.LogVerbose("Loading package references failed, solution is not loaded fully yet.");
            return null;
        }
    }

    /// <summary>Returns <see cref="ProjectName"/>.</summary>
    public override string ToString() => ProjectName;

    /// <summary>Not implemented.</summary>
    public ProjectSettings GetProjectSettings()
    {
        throw new NotImplementedException();
    }

    /// <summary>Registers the configuration provider and project-settings provider for this project scope.</summary>
    public void InitializeServices()
    {
        ConfigurationProjectSystemExtensions.GetDeveroomConfigurationProvider(this);
        this.GetProjectSettingsProvider();
    }
}
