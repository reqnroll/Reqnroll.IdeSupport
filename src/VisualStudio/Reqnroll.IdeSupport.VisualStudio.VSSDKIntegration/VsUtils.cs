#nullable disable
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.ServiceBroker;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using NuGet.VisualStudio.Contracts;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.VisualStudio.Interop;
using System.Reflection;
using System.Windows.Media;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using IServiceProvider = System.IServiceProvider;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Static helpers for interacting with the EnvDTE object model and other classic VS SDK services
/// (projects, hierarchies, output paths, MEF resolution, NuGet, status bar) from the VSSDK
/// integration.
/// </summary>
public static class VsUtils
{
    //public static ProjectItem GetProjectItemFromTextBuffer(ITextBuffer textBuffer)
    //{
    //    try
    //    {
    //        if (!textBuffer.Properties.TryGetProperty(typeof(IVsTextBuffer), out IVsTextBuffer bufferAdapter) ||
    //            bufferAdapter == null)
    //            return null;

    //        var extensibleObject = bufferAdapter as IExtensibleObject;
    //        if (extensibleObject != null)
    //        {
    //            extensibleObject.GetAutomationObject("Document", null, out object documentObj);
    //            var document = (Document) documentObj;
    //            if (document != null) return document.ProjectItem;
    //        }

    //        return null;
    //    }
    //    catch (Exception ex)
    //    {
    //        Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetProjectItemFromTextBuffer)}");
    //        return null;
    //    }
    //}

    //public static IWpfTextView GetWpfTextViewFromFilePath(string filePath, IServiceProvider serviceProvider,
    //    IVsEditorAdaptersFactoryService editorAdaptersFactoryService)
    //{
    //    if (GetVsWindowFrame(filePath, serviceProvider, out var windowFrame))
    //    {
    //        // Get the IVsTextView from the windowFrame.
    //        IVsTextView textView = VsShellUtilities.GetTextView(windowFrame);
    //        if (!IsInitialized(textView))
    //            return null;

    //        return editorAdaptersFactoryService.GetWpfTextView(textView);
    //    }

    //    return null;
    //}

    //private static IVsWindowFrame GetVsWindowFrame(string filePath, IServiceProvider serviceProvider,
    //    bool openIfNotOpened)
    //{
    //    if (VsShellUtilities.IsDocumentOpen(serviceProvider, filePath, Guid.Empty,
    //            out var _, out var _, out var windowFrame))
    //        return windowFrame;

    //    if (!openIfNotOpened)
    //        return null;

    //    VsShellUtilities.OpenDocument(serviceProvider, filePath, Guid.Empty,
    //        out var _, out var _, out windowFrame);
    //    return windowFrame;
    //}

    //private static bool GetVsWindowFrame(string filePath, IServiceProvider serviceProvider,
    //    out IVsWindowFrame windowFrame) =>
    //    VsShellUtilities.IsDocumentOpen(serviceProvider, filePath, Guid.Empty,
    //        out _, out _, out windowFrame);

    /// <summary>Opens <paramref name="filePath"/> in the editor if it is not already open.</summary>
    public static void OpenIfNotOpened(string filePath, IServiceProvider serviceProvider)
    {
        if (VsShellUtilities.IsDocumentOpen(serviceProvider, filePath, Guid.Empty,
                out _, out _, out _))
            return;

        VsShellUtilities.OpenDocument(serviceProvider, filePath, Guid.Empty,
            out _, out _, out _);
    }

    /// <summary>
    ///     IVsEditorAdaptersFactoryService.GetWpfTextView brings the text view into an inconsistent state when it is not fully
    ///     initialized (open project with files opened but not activated yet)
    /// </summary>
    private static bool IsInitialized(IVsTextView textView)
    {
        if (textView == null)
            return false;
        var propertyInfo = textView.GetType()
            .GetProperty("CurrentInitializationState", BindingFlags.Instance | BindingFlags.Public);
        if (propertyInfo == null)
            return true; // actually we don't know
        var value = propertyInfo.GetValue(textView).ToString();
        return value == "TextViewAvailable";
    }

    /// <summary>Returns the containing <see cref="Project"/> of <paramref name="projectItem"/>, or <see langword="null"/> on failure.</summary>
    public static Project GetProject(ProjectItem projectItem)
    {
        try
        {
            return projectItem?.ContainingProject;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetProject)}");
            return null;
        }
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="project"/> has a resolvable file path (i.e. is a real solution project, not a solution folder) and is not a shared project.</summary>
    /// <remarks>
    /// A solution folder is excluded because it has no path at all. A shared project (.shproj) is
    /// excluded because, although it does have one, it produces no assembly and owns no files of
    /// its own at build time -- its sources compile into every project that imports its
    /// .projitems, and sending it as a project makes the server treat it as the owner of those
    /// files (issue #735).
    /// </remarks>
    public static bool IsSolutionProject(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return !string.IsNullOrWhiteSpace(project.FullName) &&
                   Path.GetDirectoryName(project.FullName) != null &&
                   !ProjectFileTypes.IsSharedProject(project.FullName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(IsSolutionProject)}");
            return false;
        }
    }

    /// <summary>Returns the project's root folder path, falling back to the directory of its file path if the "FullPath" property is unavailable.</summary>
    public static string GetProjectFolder(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.Properties.Item("FullPath").Value.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetProjectFolder)}");
            return string.IsNullOrEmpty(project.FullName) ? null : Path.GetDirectoryName(project.FullName);
        }
    }

    /// <summary>Returns the platform name of the project's active configuration, or <see langword="null"/> on failure.</summary>
    public static string GetPlatformName(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.ConfigurationManager.ActiveConfiguration.PlatformName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetPlatformName)}");
            return null;
        }
    }

    /// <summary>Returns the "PlatformTarget" build property of the project's active configuration, or <see langword="null"/> if unset/unavailable.</summary>
    public static string GetPlatformTargetName(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (project.ConfigurationManager.ActiveConfiguration.Properties == null)
                return null;
            var platformTargetName = project.ConfigurationManager.ActiveConfiguration.Properties.Item("PlatformTarget").Value.ToString();
            if (string.IsNullOrEmpty(platformTargetName))
                return null;
            return platformTargetName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetPlatformTargetName)}");
            return null;
        }
    }

    /// <summary>Returns the project's output file name, falling back to <c>{AssemblyName}.dll</c> if unset.</summary>
    public static string GetOutputFileName(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var result = project.Properties.Item("OutputFileName").Value.ToString();
            if (string.IsNullOrWhiteSpace(result)) result = project.Properties.Item("AssemblyName").Value + ".dll";
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetOutputFileName)}");
            return null;
        }
    }

    /// <summary>Returns the "OutputPath" build property of the project's active configuration, or <see langword="null"/> if unavailable.</summary>
    public static string GetOutputPath(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (project.ConfigurationManager.ActiveConfiguration.Properties == null)
                return null;
            return project.ConfigurationManager.ActiveConfiguration.Properties.Item("OutputPath").Value.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetOutputPath)}");
            return null;
        }
    }

    /// <summary>Resolves the project's full output assembly path, falling back to scanning build output groups if the configured output path is unavailable.</summary>
    public static string GetOutputAssemblyPath(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            //DumpProperties(project.Properties, "project");
            //DumpProperties(project.ConfigurationManager.ActiveConfiguration.Properties, "ActiveConfiguration");
            var outputFileName = GetOutputFileName(project);
            if (outputFileName == null)
                return null;
            var projectFolder = GetProjectFolder(project);
            if (projectFolder == null)
                return null;
            var outputPath = GetOutputPath(project);
            if (outputPath == null)
            {
                var outputAssemblyPath = GetOutputAssemblyPathFromOutputGroups(project, outputFileName, projectFolder);
                if (outputAssemblyPath != null)
                    return outputAssemblyPath;
                return null;
            }

            return Path.Combine(projectFolder, outputPath, outputFileName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetOutputAssemblyPath)}");
            return null;
        }
    }

    private static string GetOutputAssemblyPathFromOutputGroups(Project project, string outputFileName, string projectFolder)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            DumpOutputGroups(project.ConfigurationManager.ActiveConfiguration.OutputGroups);
            var primaryOutputGroup = project.ConfigurationManager.ActiveConfiguration.OutputGroups.Item("Built");
            if (primaryOutputGroup == null)
                return null;
            var fileUrls = (primaryOutputGroup.FileURLs as object[])?.OfType<string>();
            if (fileUrls == null)
                return null;
            string GetUriPath(string url)
            {
                if (url.StartsWith("file://") && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return uri.LocalPath;
                return url;
            }
            var filePath = fileUrls.Select(GetUriPath).FirstOrDefault(path => path.EndsWith(Path.DirectorySeparatorChar + outputFileName));
            if (filePath == null)
                return null;

            var objPathSegment = @"\obj\";
            var objIndex = filePath.LastIndexOf(objPathSegment, StringComparison.CurrentCultureIgnoreCase);
            if (objIndex >= 0)
            {
                filePath = filePath
                    .Remove(objIndex, objPathSegment.Length)
                    .Insert(objIndex, @"\bin\");
            }

            return filePath;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetOutputAssemblyPathFromOutputGroups)}");
            return null;
        }
    }

    /// <summary>Returns the project's single "TargetFrameworkMoniker" property, or <see langword="null"/> on failure.</summary>
    public static string GetTargetFrameworkMoniker(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.Properties.Item("TargetFrameworkMoniker").Value.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetTargetFrameworkMoniker)}");
            return null;
        }
    }

    /// <summary>Returns the project's "TargetFrameworkMonikers" property (multi-targeting), or <see langword="null"/> on failure.</summary>
    public static string GetTargetFrameworkMonikers(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return project.Properties.Item("TargetFrameworkMonikers").Value.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetTargetFrameworkMonikers)}");
            return null;
        }
    }

    //private static void DumpProperties(Properties props, string category)
    //{
    //    if (props == null)
    //        return;
    //    var result = new StringBuilder();
    //    result.AppendLine("START PROPS: " + category);
    //    foreach (Property prop in props)
    //    {
    //        result.Append($"{prop.Name} = ");
    //        try
    //        {
    //            result.Append(prop.Value);
    //        }
    //        catch (Exception e)
    //        {
    //            result.Append(e.Message);
    //        }

    //        result.AppendLine();
    //    }

    //    result.AppendLine("END PROPS: " + category);
    //    Debug.WriteLine(result);
    //}

    private static void DumpOutputGroups(OutputGroups outputGroups)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        void DumpOutputGroup(OutputGroup outputGroup)
        {
            var canonicalName = outputGroup.CanonicalName;
            var displayName = outputGroup.DisplayName;
            var description = outputGroup.Description;
            var fileCount = outputGroup.FileCount;
            var fileNames = outputGroup.FileNames as object[];
            var fileUrls = outputGroup.FileURLs as object[];

            string GetArrayValue(object[] array)
                => array == null ? "" : string.Join(Environment.NewLine, array.Select((o, i) => $"[{i}]: {o}"));

            Debug.WriteLine($"Output group: {canonicalName}, {displayName}, {description}, {fileCount}");
            Debug.WriteLine($"File names: {Environment.NewLine}{GetArrayValue(fileNames)}");
            Debug.WriteLine($"File URLs: {Environment.NewLine}{GetArrayValue(fileUrls)}");
        }

        foreach (OutputGroup outputGroup in outputGroups)
        {
            DumpOutputGroup(outputGroup);
        }
    }

    /// <summary>Resolves a MEF component of type <typeparamref name="T"/> from the DTE's OLE service provider, returning <see langword="null"/> if resolution or composition fails.</summary>
    public static T SafeResolveMefDependency<T>(DTE dte) where T : class
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var oleServiceProvider = dte as IOleServiceProvider;
            if (oleServiceProvider == null)
                return null;
            return ResolveMefDependency<T>(new ServiceProvider(oleServiceProvider));
        }
        catch (CompositionFailedException ex)
        {
            Debug.WriteLine(ex);
            return null;
        }

        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>Resolves a MEF component of type <typeparamref name="T"/> via <see cref="SComponentModel"/> on <paramref name="serviceProvider"/>.</summary>
    public static T ResolveMefDependency<T>(IServiceProvider serviceProvider) where T : class
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModel = (IComponentModel) serviceProvider.GetService(typeof(SComponentModel));
        return componentModel?.GetService<T>();
    }

    /// <summary>Returns the MEF catalog cache folder from <see cref="SVsComponentModelHost"/>, or <see langword="null"/> if unavailable.</summary>
    public static string GetMefCatalogCacheFolder(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var componentModelHost = serviceProvider.GetService(typeof(SVsComponentModelHost)) as IVsComponentModelHost;
        if (componentModelHost == null)
            return null;

        componentModelHost.GetCatalogCacheFolder(out var folderPath);
        return folderPath;
    }

    //[DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    //public static extern int ClientToScreen(IntPtr hWnd, [In] [Out] User32Point pt);

    //public static Point? GetCaretPosition(IServiceProvider serviceProvider)
    //{
    //    try
    //    {
    //        var vsTextManager = (IVsTextManager) serviceProvider.GetService(typeof(SVsTextManager));
    //        if (vsTextManager == null)
    //            return null;

    //        ErrorHandler.ThrowOnFailure(vsTextManager.GetActiveView(Convert.ToInt32(true), null, out var activeView));
    //        if (activeView == null)
    //            return null;

    //        ErrorHandler.ThrowOnFailure(activeView.GetCaretPos(out var caretLine, out var caretColumn));

    //        var interopPoint = new POINT[1];
    //        ErrorHandler.ThrowOnFailure(activeView.GetPointOfLineColumn(caretLine + 1, caretColumn + 1, interopPoint));

    //        var p = new User32Point(interopPoint[0].x, interopPoint[0].y);
    //        ErrorHandler.ThrowOnFailure(ClientToScreen(activeView.GetWindowHandle(), p));

    //        var wpfTextView = GetWpfTextView(serviceProvider, activeView) as Visual;
    //        if (wpfTextView != null)
    //        {
    //            var target = PresentationSource.FromVisual(wpfTextView)?.CompositionTarget;
    //            if (target != null)
    //            {
    //                var transformedPoint = target.TransformFromDevice.Transform(new Point(p.x, p.y));
    //                return transformedPoint;
    //            }
    //        }

    //        return new Point(p.x, p.y);
    //    }
    //    catch (Exception ex)
    //    {
    //        Debug.WriteLine(ex);
    //        return null;
    //    }
    //}

    //private static IWpfTextView GetWpfTextView(IServiceProvider serviceProvider, IVsTextView activeView)
    //{
    //    var editorAdaptersFactoryService = ResolveMefDependency<IVsEditorAdaptersFactoryService>(serviceProvider);
    //    return editorAdaptersFactoryService?.GetWpfTextView(activeView);
    //}

    /// <summary>Resolves the <see cref="IVsHierarchy"/> for <paramref name="project"/> via the solution service, or <see langword="null"/> on failure.</summary>
    public static IVsHierarchy GetHierarchyFromProject(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var serviceProvider = new ServiceProvider(project.DTE as IOleServiceProvider);
            if (!(serviceProvider.GetService(typeof(SVsSolution)) is IVsSolution solution))
                return null;
            if (!ErrorHandler.Succeeded(solution.GetProjectOfUniqueName(project.FullName, out var hierarchy)))
                return null;
            return hierarchy;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>Returns the automation <see cref="Project"/> object for the given <paramref name="hierarchy"/>'s root item, or <see langword="null"/> if not resolvable.</summary>
    public static Project GetProjectFromHierarchy(IVsHierarchy hierarchy)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (hierarchy is null)
                return null;

            if (!ErrorHandler.Succeeded(hierarchy.GetProperty(
                    VSConstants.VSITEMID_ROOT,
                    (int) __VSHPROPID.VSHPROPID_ExtObject,
                    out var projectObj)))
                return null;

            return projectObj as Project;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    //public static string SafeGetProjectFilePath(IVsHierarchy vsHierarchy)
    //{
    //    var project = GetProjectFromHierarchy(vsHierarchy);
    //    return project?.FullName;
    //}

    /// <summary>Reads an MSBuild property value from the project file via <see cref="IVsBuildPropertyStorage"/>, or <see langword="null"/> on failure.</summary>
    public static string GetMsBuildPropertyValue(Project project, string propertyName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var hierarchy = GetHierarchyFromProject(project);
            if (!(hierarchy is IVsBuildPropertyStorage propertyStorage))
                return null;

            if (ErrorHandler.Succeeded(
                    propertyStorage.GetPropertyValue(propertyName,
                        project.ConfigurationManager.ActiveConfiguration.ConfigurationName,
                        (uint) _PersistStorageType.PST_PROJECT_FILE, out var propValue)))
                return propValue;

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    /// <summary>Recursively enumerates all project items (including nested items) in <paramref name="project"/>.</summary>
    public static IEnumerable<ProjectItem> GetProjectItems(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetProjectItems(project.ProjectItems);
    }

    private static IEnumerable<ProjectItem> GetProjectItems(ProjectItems projectItems)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (ProjectItem projectItem in projectItems)
        {
            yield return projectItem;
            if (projectItem.ProjectItems != null)
                foreach (var subProjectItem in GetProjectItems(projectItem.ProjectItems))
                    yield return subProjectItem;
        }
    }

    /// <summary>Enumerates the project items in <paramref name="project"/> that represent physical files on disk.</summary>
    public static IEnumerable<ProjectItem> GetPhysicalFileProjectItems(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetProjectItems(project).Where(IsPhysicalFile);
    }

    /// <summary>Finds the physical-file project item in <paramref name="project"/> whose path matches <paramref name="filePath"/> (case-insensitive), or <see langword="null"/>.</summary>
    public static ProjectItem FindProjectItemByFilePath(Project project, string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return GetPhysicalFileProjectItems(project)
            .FirstOrDefault(pi => string.Equals(filePath, GetFilePath(pi), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="projectItem"/>'s kind is a physical file.</summary>
    public static bool IsPhysicalFile(ProjectItem projectItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return string.Equals(projectItem.Kind,
            VSConstants.GUID_ItemType_PhysicalFile.ToString("B"), StringComparison.InvariantCultureIgnoreCase);
    }

    /// <summary>Returns the file system path of <paramref name="projectItem"/>, or <see langword="null"/> if it isn't a physical file.</summary>
    public static string GetFilePath(ProjectItem projectItem)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsPhysicalFile(projectItem))
            return null;

        return projectItem.FileNames[1];
    }

    /// <summary>
    /// Returns every real project in the solution, descending into solution folders to arbitrary
    /// depth. <see cref="EnvDTE.Solution.Projects"/> is flat: it yields solution folders as
    /// <see cref="Project"/> objects, and the projects nested inside them are reachable only via
    /// <see cref="ProjectItem.SubProject"/> — so enumerating it directly silently drops every
    /// nested project (issue #729). Solution folders themselves are not returned.
    /// </summary>
    public static IEnumerable<Project> GetAllProjects(Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var roots = solution?.Projects;
        if (roots == null)
            return Array.Empty<Project>();

        // Materialised rather than lazy: the traversal touches COM, so it must complete while the
        // caller is still on the UI thread rather than at some later point of enumeration.
        return ProjectHierarchyWalker
            .Flatten(roots.OfType<Project>(), GetProjectKey, GetSubProjects)
            .ToList();
    }

    /// <summary>
    /// A project's identity for hierarchy de-duplication: its file path, or <see langword="null"/>
    /// when the node is a solution folder (or a project whose path cannot be read), which marks it
    /// as "descend into but do not return".
    /// </summary>
    private static string GetProjectKey(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            // Kind first: a solution folder is never a project, even on the project types that
            // report a non-empty FullName for one.
            return !IsSolutionFolder(project) && IsSolutionProject(project)
                ? project.FullName
                : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetProjectKey)}");
            return null;
        }
    }

    /// <summary>The well-known DTE project kind GUID for a solution folder (<c>vsProjectKindSolutionFolder</c>).</summary>
    private const string SolutionFolderKind = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";

    /// <summary>
    /// Best-effort read of a solution folder's nested sub-projects.
    /// <para>
    /// Only solution folders are descended into. A real project's <see cref="Project.ProjectItems"/>
    /// is its file list, which can run to thousands of entries; probing <c>SubProject</c> on each
    /// would cost a COM round-trip per file on the UI thread, for nodes that cannot contain a
    /// nested project anyway.
    /// </para>
    /// <para>
    /// Returns empty rather than throwing: <see cref="Project.ProjectItems"/> is null for some
    /// project types and can throw for unloaded ones, and one bad node must not abort the walk.
    /// </para>
    /// </summary>
    private static IEnumerable<Project> GetSubProjects(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (!IsSolutionFolder(project))
            return Array.Empty<Project>();

        ProjectItems items;
        try
        {
            items = project.ProjectItems;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(GetSubProjects)}");
            return Array.Empty<Project>();
        }

        if (items == null)
            return Array.Empty<Project>();

        var result = new List<Project>();
        foreach (var item in items.OfType<ProjectItem>())
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Project subProject;
            try
            {
                subProject = item.SubProject;
            }
            catch
            {
                continue;   // not a container item, or an unloaded project
            }

            if (subProject != null)
                result.Add(subProject);
        }

        return result;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="project"/> is a solution folder — the
    /// only kind of node that can contain nested projects.
    /// </summary>
    private static bool IsSolutionFolder(Project project)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return string.Equals(project.Kind, SolutionFolderKind, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex, $"{nameof(VsUtils)}.{nameof(IsSolutionFolder)}");
            return false;
        }
    }

    /// <summary>Returns the DTE version string (e.g. "17.0"), falling back to a hard-coded default if unavailable.</summary>
    public static string GetVsMainVersion(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        const string defaultMainVersion = "17.0";
        try
        {
            var dte = (DTE) serviceProvider.GetService(typeof(DTE));
            return dte?.Version ?? defaultMainVersion;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return defaultMainVersion;
        }
    }

    // https://stackoverflow.com/a/55039958
    /// <summary>Returns the VS product display version (e.g. "17.9.1") via <see cref="IVsAppId"/>, falling back to <see cref="GetVsMainVersion"/> on failure.</summary>
    public static string GetVsProductDisplayVersionSafe(IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var vsAppId = serviceProvider.GetService<IVsAppId>(typeof(SVsAppId));
            vsAppId.GetProperty((int) VSAPropID.VSAPROPID_ProductDisplayVersion, out var productDisplayVersion);

            var displayVersion = productDisplayVersion as string;
            return displayVersion ?? GetVsMainVersion(serviceProvider);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return GetVsMainVersion(serviceProvider);
        }
    }

    /// <summary>
    /// Retrieves the installed NuGet packages for the project via the NuGet brokered service; blocks
    /// synchronously on the async call.
    /// </summary>
    /// <exception cref="NuGetProjectNotReadyException">
    /// NuGet reports <see cref="InstalledPackageResultStatus.ProjectNotReady"/> -- the project
    /// hasn't been nominated/restored yet. Routine during solution load (issue #690); callers should
    /// treat this as "try again once restore finishes," not as a failure worth surfacing to the user.
    /// </exception>
    public static IEnumerable<NuGetInstalledPackage> GetInstalledNuGetPackages(IServiceProvider serviceProvider,
        string projectFullName)
    {
        return ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var solution = serviceProvider.GetService<SVsSolution, IVsSolution>();
            int result = solution.GetProjectOfUniqueName(projectFullName, out IVsHierarchy project);
            if (result != VSConstants.S_OK)
                throw new Exception(
                    $"Error calling {nameof(IVsSolution)}.{nameof(IVsSolution.GetProjectOfUniqueName)}: {result}");

            result = solution.GetGuidOfProject(project, out Guid projectGuid);
            if (result != VSConstants.S_OK)
                throw new Exception(
                    $"Error calling {nameof(IVsSolution)}.{nameof(IVsSolution.GetGuidOfProject)}: {result}");

            var serviceBrokerContainer =
                serviceProvider.GetService<SVsBrokeredServiceContainer, IBrokeredServiceContainer>();
            var serviceBroker = serviceBrokerContainer.GetFullAccessServiceBroker();

            var projectService =
                await serviceBroker.GetProxyAsync<INuGetProjectService>(NuGetServices.NuGetProjectServiceV1);
            using (projectService as IDisposable)
            {
                var packagesResult =
                    await projectService.GetInstalledPackagesAsync(projectGuid, CancellationToken.None);
                if (packagesResult.Status == InstalledPackageResultStatus.ProjectNotReady)
                    throw new NuGetProjectNotReadyException();
                if (packagesResult.Status != InstalledPackageResultStatus.Successful)
                    throw new Exception("Unexpected result from GetInstalledPackagesAsync: " + packagesResult.Status);
                return packagesResult.Packages;
            }
        });
    }

    //[StructLayout(LayoutKind.Sequential)]
    //public class User32Point
    //{
    //    public int x;
    //    public int y;

    //    public User32Point()
    //    {
    //        x = 0;
    //        y = 0;
    //    }

    //    public User32Point(int x, int y)
    //    {
    //        this.x = x;
    //        this.y = y;
    //    }
    //}

    /// <summary>
    /// Shows a temporary message in the VS status bar. Centralises the VSSDK
    /// <see cref="IVsStatusbar"/> dependency here so it can be replaced with
    /// <c>Extensibility.Shell().StatusBar.ShowMessageAsync</c> when the VS
    /// Extensibility SDK supports it.
    /// Must be called from the UI thread.
    /// </summary>
#pragma warning disable VSTHRD010 // Callers must be on the UI thread (commands, package init, etc.).
    public static void ShowStatusBarMessage(string message)
    {
        var statusBar = ServiceProvider.GlobalProvider.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
        statusBar?.SetText(message);
    }
#pragma warning restore VSTHRD010
}
