using Microsoft.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.Extension;
using Reqnroll.IdeSupport.VisualStudio.Extension.Documents;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// Covers the GUID half of <see cref="MiscellaneousFilesProject"/>'s decision rule. The COM-identity
/// fallback needs a live <c>IVsHierarchy</c> and <c>SVsExternalFilesManager</c>, so it is not covered here.
/// </summary>
public class MiscellaneousFilesProjectTests
{
    private static readonly Guid MiscFiles = VSConstants.CLSID_MiscellaneousFilesProject;
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public void The_SDK_constant_is_the_value_the_old_hard_coded_literal_held()
    {
        MiscFiles.Should().Be(new Guid("A2FE74E1-B743-11d0-AE1A-00A0C90FFFC3"));
    }

    [Fact]
    public void Matches_on_the_project_id_guid()
    {
        MiscellaneousFilesProject.MatchByGuid(VSConstants.S_OK, MiscFiles, VSConstants.S_OK, Other)
            .Should().Be(MiscellaneousFilesMatch.ProjectIdGuid);
    }

    [Fact]
    public void Matches_on_the_type_guid_when_the_project_id_differs()
    {
        MiscellaneousFilesProject.MatchByGuid(VSConstants.S_OK, Other, VSConstants.S_OK, MiscFiles)
            .Should().Be(MiscellaneousFilesMatch.TypeGuid);
    }

    [Fact]
    public void No_match_when_neither_guid_is_the_misc_files_clsid()
    {
        MiscellaneousFilesProject.MatchByGuid(VSConstants.S_OK, Other, VSConstants.S_OK, Other)
            .Should().Be(MiscellaneousFilesMatch.None);
    }

    [Fact]
    public void A_failed_property_read_is_ignored_even_if_its_out_value_looks_like_a_match()
    {
        MiscellaneousFilesProject.MatchByGuid(VSConstants.E_FAIL, MiscFiles, VSConstants.E_NOTIMPL, MiscFiles)
            .Should().Be(MiscellaneousFilesMatch.None);
    }
}
