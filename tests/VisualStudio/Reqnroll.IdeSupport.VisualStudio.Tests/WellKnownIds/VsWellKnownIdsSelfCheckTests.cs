using Microsoft.VisualStudio;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Outcome = Reqnroll.IdeSupport.VisualStudio.VsWellKnownIdsSelfCheck.Outcome;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// Covers the decision rule <see cref="VsWellKnownIdsSelfCheck"/> applies to one
/// <c>IVsCmdNameMapping.MapGUIDIDToName</c> result, and the list of identifiers it checks. The COM call
/// itself needs a running VS, so it is not covered here.
/// </summary>
public class VsWellKnownIdsSelfCheckTests
{
    [Fact]
    public void Match_when_the_resolved_name_is_the_expected_one()
    {
        VsWellKnownIdsSelfCheck.Evaluate("Edit.ToggleLineComment", VSConstants.S_OK, "Edit.ToggleLineComment")
            .Should().Be(Outcome.Match);
    }

    [Theory]
    [InlineData("edit.togglelinecomment")]
    [InlineData(".Edit.ToggleLineComment")]
    [InlineData(" Edit.ToggleLineComment ")]
    public void Match_ignores_case_surrounding_whitespace_and_a_leading_dot(string actual)
    {
        VsWellKnownIdsSelfCheck.Evaluate("Edit.ToggleLineComment", VSConstants.S_OK, actual)
            .Should().Be(Outcome.Match);
    }

    [Fact]
    public void NameMismatch_when_the_id_resolves_to_a_different_command()
    {
        VsWellKnownIdsSelfCheck.Evaluate("Edit.ToggleLineComment", VSConstants.S_OK, "Edit.ToggleBlockComment")
            .Should().Be(Outcome.NameMismatch);
    }

    [Fact]
    public void NotFound_when_the_mapping_returns_S_FALSE()
    {
        VsWellKnownIdsSelfCheck.Evaluate("Edit.ToggleLineComment", VSConstants.S_FALSE, null)
            .Should().Be(Outcome.NotFound);
    }

    [Fact]
    public void NotFound_when_the_mapping_fails_even_if_a_name_came_back()
    {
        VsWellKnownIdsSelfCheck.Evaluate("Edit.ToggleLineComment", VSConstants.E_FAIL, "Edit.ToggleLineComment")
            .Should().Be(Outcome.NotFound);
    }

    [Fact]
    public void NotFound_when_the_name_is_empty()
    {
        VsWellKnownIdsSelfCheck.Evaluate("Edit.ToggleLineComment", VSConstants.S_OK, "")
            .Should().Be(Outcome.NotFound);
    }

    [Fact]
    public void Checks_every_undocumented_command_id_the_extension_hard_codes()
    {
        var checkedIds = VsWellKnownIdsSelfCheck.ExpectedCommands.Select(c => (c.Group, c.Id)).ToList();

        checkedIds.Should().Contain((VsWellKnownIds.EditorCommandSet, VsWellKnownIds.CmdIdToggleLineComment));
        checkedIds.Should().Contain((TestExplorerCommandIds.CommandSet, (uint)TestExplorerCommandIds.RunCommandId));
        checkedIds.Should().Contain((TestExplorerCommandIds.CommandSet, (uint)TestExplorerCommandIds.DebugCommandId));
        checkedIds.Should().Contain((TestExplorerCommandIds.CommandSet, (uint)TestExplorerCommandIds.SyncCommandId));
        checkedIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_comment_filter_uses_the_checked_ToggleLineComment_id()
    {
        CommentToggleCommandFilter.EditorCommandSet.Should().Be(VsWellKnownIds.EditorCommandSet);
        CommentToggleCommandFilter.CmdIdToggleLineComment.Should().Be(VsWellKnownIds.CmdIdToggleLineComment);
    }
}
