using Reqnroll.IdeSupport.VisualStudio.Wizards.UI.ViewModels.WizardDialogs;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.ViewModels;

public class UpgradeDialogViewModelTests
{
    [Fact]
    public void Constructor_creates_two_pages()
    {
        var sut = new UpgradeDialogViewModel("2.0.0", "# Changelog");

        sut.Pages.Should().HaveCount(2);
    }

    [Fact]
    public void Constructor_sets_changes_page_and_community_page_names()
    {
        var sut = new UpgradeDialogViewModel("2.0.0", "# Changelog");

        sut.Pages[0].Name.Should().Be("Changes");
        sut.Pages[1].Name.Should().Be("Community");
    }

    [Fact]
    public void Changes_page_contains_version_header_and_changelog_content()
    {
        var sut = new UpgradeDialogViewModel("2.4.1", "# New Stuff\n- item");

        var changesPage = sut.Pages[0].Should().BeOfType<MarkDownWizardPageViewModel>().Subject;
        changesPage.Text.Should().Contain("Reqnroll Updated to v2.4.1");
        changesPage.Text.Should().Contain("# New Stuff");
        changesPage.Text.Should().Contain("- item");
    }

    [Fact]
    public void Changes_page_promotes_markdown_heading_levels_by_one()
    {
        var changeLog = "# H1\n## H2\n### H3";
        var sut = new UpgradeDialogViewModel("3.0.0", changeLog);

        var changesPage = sut.Pages[0].Should().BeOfType<MarkDownWizardPageViewModel>().Subject;
        changesPage.Text.Should().Contain("## H1");
        changesPage.Text.Should().Contain("### H2");
        changesPage.Text.Should().Contain("#### H3");
    }

    [Fact]
    public void Community_page_contains_community_info_text()
    {
        var sut = new UpgradeDialogViewModel("2.0.0", "# Changes");

        var communityPage = sut.Pages[1].Should().BeOfType<MarkDownWizardPageViewModel>().Subject;
        communityPage.Text.Should().Contain(UpgradeDialogViewModel.COMMUNITY_INFO_HEADER.Trim());
        communityPage.Text.Should().Contain("github.com/reqnroll/Reqnroll.IdeSupport/issues");
    }

    [Fact]
    public void Constructor_sets_dialog_title_and_finish_button_label()
    {
        var sut = new UpgradeDialogViewModel("2.0.0", "# Changes");

        sut.DialogTitle.Should().Be("Welcome to Reqnroll");
        sut.FinishButtonLabel.Should().Be("Close");
    }
}
