#nullable enable

using Microsoft.VisualStudio;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Documents;

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
/// <c>CLSID_MiscellaneousFilesProject</c> (<c>vsshell.h</c>) is the project's <i>class</i> ID, yet the
/// code compares it with the per-instance <c>VSHPROPID_ProjectIDGuid</c>, which has never been verified
/// against a running VS. The type GUID is checked too, and when neither GUID matches the caller compares
/// COM identity with the documented <c>SVsExternalFilesManager</c> service
/// (<see cref="MiscellaneousFilesMatch.ExternalFilesProjectIdentity"/>). For now only
/// <see cref="MiscellaneousFilesMatch.ProjectIdGuid"/> drives behaviour; the other signals are logged
/// so a live session can show which one is right before behaviour changes.
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
