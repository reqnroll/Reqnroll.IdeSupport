// VsIntegration layer — VS SDK (IWizard, DTE, EnvDTE) references expected here.
#nullable disable
using EnvDTE;
using Microsoft.VisualStudio.TemplateWizard;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.VisualStudio.Package.ProjectSystem;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Core;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Ported from VsProjectScopeWizard. Implements VS IWizard and bridges
/// to ITemplateWizard / IWizardContext. Concrete subclasses only need to
/// specify the TWizard type parameter and optionally override ResolveWizard.
/// </summary>
public abstract class VsTemplateWizardBase<TWizard> : IWizard
    where TWizard : class, ITemplateWizard
{
    protected bool _isValidRun;
    protected Project _project;
    protected TWizard _wizard;
    protected IWizardContext _wizardContext;

    protected IIdeSupportLogger Logger =>
        (_wizardContext as VsWizardContext) != null
            ? null  // logger is accessed through VS infra; null-safe below
            : null;

    /// <summary>
    /// VS entry point (<see cref="IWizard.RunStarted"/>). Resolves the active project/IDE
    /// scope, builds the <see cref="IWizardContext"/> and dispatches to the
    /// wizard-specific <see cref="RunStarted(Project, IWizardContext, TWizard)"/> overload.
    /// Backs out (cleaning up generated project files) if anything along the way fails.
    /// </summary>
    public virtual void RunStarted(object automationObjectDte,
        Dictionary<string, string> replacementsDictionary,
        WizardRunKind runKind, object[] customParams)
    {
        _isValidRun = false;

        if (runKind != WizardRunKind.AsNewItem && runKind != WizardRunKind.AsNewProject)
            return;

        bool isAddNewItem = runKind == WizardRunKind.AsNewItem;
        var dte = automationObjectDte as DTE;
        if (dte == null) return;

        Project project = null;
        IProjectScope projectScope = null;
        IIdeScope ideScope = null;
        string targetFolder = null;
        string targetFileName = null;

        if (isAddNewItem)
        {
            project = GetActiveProject(dte);
            if (project == null) return;

            projectScope = GetProjectScope(project);
            if (projectScope == null) return;

            ideScope = projectScope.IdeScope;
            targetFolder = GetTargetFolder(project);
            targetFileName = replacementsDictionary["$rootname$"];
        }
        else
        {
            ideScope = VsUtils.SafeResolveMefDependency<IIdeScope>(dte);
            if (ideScope == null) return;
        }

        var templateFolder = GetTemplateFolder(customParams);
        if (templateFolder == null) return;

        _wizard = ResolveWizard(dte);
        if (_wizard == null) return;

        var vsUiShell = VsUtils.SafeResolveMefDependency<Microsoft.VisualStudio.Shell.Interop.IVsUIShell>(dte);
        var dialogService = new VsWizardDialogService(vsUiShell);
        var telemetry = new VsWizardTelemetry(ideScope.TelemetryService);

        _wizardContext = new VsWizardContext(
            isAddNewItem, projectScope, ideScope, templateFolder,
            targetFolder, targetFileName, replacementsDictionary,
            dialogService, telemetry);

        _project = project;

        try
        {
            _isValidRun = RunStarted(project, _wizardContext, _wizard);
        }
        catch (Exception ex)
        {
            ideScope.Actions.ShowError("Error during project generation", ex);
            _isValidRun = false;
        }

        if (!_isValidRun && !isAddNewItem)
        {
            var projectDirectory = replacementsDictionary["$destinationdirectory$"];
            var solutionDirectory = replacementsDictionary["$solutiondirectory$"];
            CleanupProjectFiles(projectDirectory, solutionDirectory);
            throw new WizardBackoutException();
        }
    }

    /// <summary>Returns whether the wizard run is valid and the item should be added.</summary>
    public virtual bool ShouldAddProjectItem(string filePath) => _isValidRun;

    /// <summary>No-op hook from <see cref="IWizard"/>; nothing to do once the project is generated.</summary>
    public virtual void ProjectFinishedGenerating(Project project) { }

    /// <summary>
    /// Applies any custom-tool, build-action, and copy-to-output-directory replacements
    /// recorded in the wizard context to the newly generated project item.
    /// </summary>
    public virtual void ProjectItemFinishedGenerating(ProjectItem projectItem)
    {
        if (!_isValidRun) return;

        ApplyReplacementProperty(projectItem, WizardContextKeys.CustomToolSettingKey,
            pi => pi.Properties.Item("CustomTool").Value = _wizardContext.ReplacementsDictionary[WizardContextKeys.CustomToolSettingKey]);
        ApplyReplacementProperty(projectItem, WizardContextKeys.BuildActionKey,
            pi => pi.Properties.Item("ItemType").Value = _wizardContext.ReplacementsDictionary[WizardContextKeys.BuildActionKey]);
        ApplyReplacementProperty(projectItem, WizardContextKeys.CopyToOutputDirectoryKey, pi =>
        {
            uint value = _wizardContext.ReplacementsDictionary[WizardContextKeys.CopyToOutputDirectoryKey] switch
            {
                "Always" => 1u,
                "PreserveNewest" => 2u,
                _ => 0u
            };
            pi.Properties.Item("CopyToOutputDirectory").Value = value;
        });
    }

    /// <summary>No-op hook from <see cref="IWizard"/>; nothing to do before the file opens.</summary>
    public virtual void BeforeOpeningFile(ProjectItem projectItem) { }

    /// <summary>Clears the per-run wizard state after VS finishes the wizard run.</summary>
    public virtual void RunFinished()
    {
        _wizard = null;
        _wizardContext = null;
        _isValidRun = false;
        _project = null;
    }

    protected virtual bool RunStarted(Project project, IWizardContext context, TWizard wizard) =>
        wizard.RunStarted(context);

    protected virtual TWizard ResolveWizard(DTE dte) =>
        VsUtils.SafeResolveMefDependency<TWizard>(dte);

    private void ApplyReplacementProperty(ProjectItem projectItem, string key, Action<ProjectItem> apply)
    {
        if (_wizardContext.ReplacementsDictionary.ContainsKey(key))
        {
            try { apply(projectItem); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
    }

    private Project GetActiveProject(DTE dte)
    {
        var activeProjects = dte.ActiveSolutionProjects as Array;
        if (activeProjects == null || activeProjects.Length == 0) return null;
        return activeProjects.GetValue(0) as Project;
    }

    private string GetTargetFolder(Project project)
    {
        var dteSelectedItems = project.DTE.SelectedItems;
        if (dteSelectedItems.MultiSelect) return null;
        var selectedItem = dteSelectedItems.Item(1);
        var selectedProjectItem = selectedItem.ProjectItem;
        if (selectedProjectItem != null &&
            selectedProjectItem.Kind == Constants.vsProjectItemKindPhysicalFolder)
            return selectedProjectItem.FileNames[1];
        if (selectedItem.Project?.Name == project.Name)
            return VsUtils.GetProjectFolder(project);
        return null;
    }

    private IProjectScope GetProjectScope(Project project)
    {
        var projectSystem = VsUtils.SafeResolveMefDependency<IIdeScope>(project.DTE)
            as IVsIdeScope;
        return projectSystem?.GetProjectScope(project);
    }

    private string GetTemplateFolder(object[] customParams)
    {
        if (customParams.Length == 0) return null;
        return customParams[0] is string templatePath ? Path.GetDirectoryName(templatePath) : null;
    }

    private void CleanupProjectFiles(string projectDirectory, string solutionDirectory)
    {
        if (Directory.Exists(projectDirectory)) Directory.Delete(projectDirectory, true);
        if (projectDirectory != solutionDirectory &&
            Directory.Exists(solutionDirectory) &&
            !Directory.EnumerateFileSystemEntries(solutionDirectory).Any())
            Directory.Delete(solutionDirectory);
    }
}
