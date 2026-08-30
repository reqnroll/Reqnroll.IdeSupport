// VsIntegration layer
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.VisualStudio.Wizards.Core;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.VsIntegration;

/// <summary>
/// Ported from VsReqnrollProjectWizard.
/// ReqnrollProjectTemplateWizard is resolved via MEF from the original
/// extension's component model so it can receive IIdeSupportWindowManager
/// and ITelemetryService — these are replaced by the IWizardContext
/// services constructed in VsTemplateWizardBase.RunStarted.
/// </summary>
public class VsReqnrollProjectWizard : VsTemplateWizardBase<ReqnrollProjectTemplateWizard>
{
    protected override ReqnrollProjectTemplateWizard ResolveWizard(EnvDTE.DTE dte)
    {
        // Build the wizard directly with the VS-backed services.
        // These are created fresh per wizard run in RunStarted via the context,
        // but we need them here too for the constructor. We create temporary
        // instances from the DTE; the context will be passed to RunStarted.
        var ideScope = VsUtils.SafeResolveMefDependency<IIdeScope>(dte);
        if (ideScope == null) return null!;

        var vsUiShell = VsUtils.SafeResolveMefDependency<Microsoft.VisualStudio.Shell.Interop.IVsUIShell>(dte);
        var dialogService = new VsWizardDialogService(vsUiShell);
        var telemetry = new VsWizardTelemetry(ideScope.TelemetryService);

        return new ReqnrollProjectTemplateWizard(dialogService, telemetry);
    }
}
