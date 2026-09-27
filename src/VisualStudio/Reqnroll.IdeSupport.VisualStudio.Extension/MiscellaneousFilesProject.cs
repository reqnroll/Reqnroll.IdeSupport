#nullable enable

using Microsoft.VisualStudio;

namespace Reqnroll.IdeSupport.VisualStudio.Extension;

/// <summary>Which signal identified a hierarchy as the Miscellaneous Files project.</summary>
internal enum MiscellaneousFilesMatch
{
    /// <summary>Not the Miscellaneous Files project.</summary>
    None,

    /// <summary><c>VSHPROPID_ProjectIDGuid</c> equals <c>CLSID_MiscellaneousFilesProject</c>.</summary>
    ProjectIdGuid,

    /// <summary><c>VSHPROPID_TypeGuid</c> equals <c>CLSID_MiscellaneousFilesProject</c>.</summary>
    TypeGuid,

    /// <summary>Same COM object as <c>IVsExternalFilesManager.GetExternalFilesProject</c>.</summary>
    ExternalFilesProjectIdentity,
}

/// <summary>
/// Decision rule for recognising the Miscellaneous Files project from raw
/// <c>IVsHierarchy.GetGuidProperty</c> results, split out of <see cref="VsStubFrameInitializer"/> so it
/// can be tested without the UI thread or a live hierarchy.
/// </summary>
/// <remarks>
/// <c>CLSID_MiscellaneousFilesProject</c> (<c>vsshell.h</c>) is the project's <i>class</i> ID. The
/// code used to compare it only with the per-instance <c>VSHPROPID_ProjectIDGuid</c>, which was never
/// verified against a running VS. The type GUID is checked too. Only when neither GUID matches does
/// the caller fall back to comparing COM identity with the documented
/// <c>SVsExternalFilesManager</c> service (<see cref="MiscellaneousFilesMatch.ExternalFilesProjectIdentity"/>).
/// </remarks>
internal static class MiscellaneousFilesProject
{
    /// <summary>Classifies a hierarchy from its project-ID and type GUID property results.</summary>
    public static MiscellaneousFilesMatch MatchByGuid(int projectIdHr, Guid projectId, int typeGuidHr, Guid typeGuid)
    {
        if (ErrorHandler.Succeeded(projectIdHr) && projectId == VSConstants.CLSID_MiscellaneousFilesProject)
            return MiscellaneousFilesMatch.ProjectIdGuid;
        if (ErrorHandler.Succeeded(typeGuidHr) && typeGuid == VSConstants.CLSID_MiscellaneousFilesProject)
            return MiscellaneousFilesMatch.TypeGuid;
        return MiscellaneousFilesMatch.None;
    }
}
