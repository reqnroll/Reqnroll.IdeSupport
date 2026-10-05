// VsIntegration layer — VS SDK references expected here.
#nullable disable
using EnvDTE;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Abstractions;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Ported from VsSimulatedItemAddProjectScopeWizard.
/// Handles SDK-style projects where VS cannot add items via the normal
/// template mechanism — instead the file is copied manually and opened.
/// </summary>
public abstract class VsSimulatedItemAddWizardBase<TWizard> : VsTemplateWizardBase<TWizard>
    where TWizard : class, ITemplateWizard
{
    private bool _enableSimulatedItemAdd;
    private SafeDispatcherTimer _openFileTimer;
    private string _templateFileName;
    private bool _usingMicrosoftNetSdk;

    protected override bool RunStarted(Project project, IWizardContext context, TWizard wizard)
    {
        _usingMicrosoftNetSdk = GetUsingMicrosoftNETSdk(project);
        _enableSimulatedItemAdd =
            context.IsAddNewItem &&
            _usingMicrosoftNetSdk &&
            context.TargetFolder != null;

        if (_enableSimulatedItemAdd)
            Debug.WriteLine($"Using simulated item add for project '{project.Name}'");

        return base.RunStarted(project, context, wizard);
    }

    private bool GetUsingMicrosoftNETSdk(Project project)
    {
        var propValue = VsUtils.GetMsBuildPropertyValue(project, "UsingMicrosoftNETSdk");
        return string.Equals(propValue, "true", StringComparison.InvariantCultureIgnoreCase);
    }

    /// <summary>
    /// Records the template file name and suppresses VS's normal item-add
    /// mechanism when simulated item add is in effect (the file is copied manually instead).
    /// </summary>
    public override bool ShouldAddProjectItem(string filePath)
    {
        _templateFileName = filePath;
        return base.ShouldAddProjectItem(filePath) && !_enableSimulatedItemAdd;
    }

    /// <summary>
    /// When simulated item add was used, copies the template file (resolving
    /// template parameters) into the target folder and schedules it to be opened.
    /// </summary>
    public override void RunFinished()
    {
        if (_isValidRun && _enableSimulatedItemAdd && _templateFileName != null)
        {
            var targetFile = Path.Combine(_wizardContext.TargetFolder, _wizardContext.TargetFileName);
            var sourceFile = Path.Combine(_wizardContext.TemplateFolder, _templateFileName);
            CopyWithTemplateParamResolution(sourceFile, targetFile);
            ScheduleOpenFile(targetFile);
        }

        base.RunFinished();
    }

    private void CopyWithTemplateParamResolution(string sourceFile, string targetFile)
    {
        var ideScope = (_wizardContext as VsWizardContext) != null
            ? GetIdeScope()
            : null;
        if (ideScope?.FileSystem == null) return;

        var replacements = new Dictionary<string, string>(_wizardContext.ReplacementsDictionary)
        {
            ["$itemname$"] = Path.GetFileNameWithoutExtension(targetFile)
        };

        try
        {
            var content = ideScope.FileSystem.File.ReadAllText(sourceFile);
            var updated = Regex.Replace(content, @"\$[^\$\s]+\$",
                m => replacements.TryGetValue(m.Value, out var v) ? v : m.Value);
            ideScope.FileSystem.File.WriteAllText(targetFile, updated, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void ScheduleOpenFile(string targetFile)
    {
        var project = _project;
        _openFileTimer = SafeDispatcherTimer.CreateOneTime(1, null, null,
            () => OpenFile(targetFile, project));
        _openFileTimer.Start();
    }

    private static void OpenFile(string targetFile, Project project)
    {
        try
        {
            var projectItem = VsUtils.FindProjectItemByFilePath(project, targetFile);
            if (projectItem != null)
                project.DTE.ExecuteCommand("File.OpenFile", $"\"{targetFile}\"");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private IIdeScope GetIdeScope()
    {
        // Access through the public IdeScope property rather than private-field
        // reflection, which silently breaks if the VsWizardContext field is renamed.
        if (_wizardContext is VsWizardContext vsCtx)
            return vsCtx.IdeScope;
        return null;
    }
}
