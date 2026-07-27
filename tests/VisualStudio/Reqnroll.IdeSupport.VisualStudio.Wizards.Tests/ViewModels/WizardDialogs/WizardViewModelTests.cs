namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.ViewModels.WizardDialogs;

public class WizardViewModelTests
{
    private static WizardViewModel CreateWithPages(int count)
    {
        var pages = new WizardPageViewModel[count];
        for (int i = 0; i < count; i++)
            pages[i] = new WizardPageViewModel($"Page{i}");
        return new WizardViewModel("Finish", "Title", pages);
    }

    [Fact]
    public void Constructor_activates_the_first_page()
    {
        var sut = CreateWithPages(3);

        sut.ActivePage.Should().BeSameAs(sut.Pages[0]);
        sut.ActivePageIndex.Should().Be(0);
        sut.Pages[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public void Constructor_marks_the_first_page_as_visited()
    {
        var sut = CreateWithPages(3);

        sut.VisitedPages.Should().ContainSingle().Which.Should().BeSameAs(sut.Pages[0]);
    }

    [Fact]
    public void PreviousCommand_cannot_execute_on_the_first_page()
    {
        var sut = CreateWithPages(3);

        sut.PreviousCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void NextCommand_cannot_execute_on_the_last_page()
    {
        var sut = CreateWithPages(2);

        sut.NextCommand.Execute(null);

        sut.IsOnLastPage.Should().BeTrue();
        sut.NextCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void NextCommand_moves_to_the_next_page_and_deactivates_the_previous_one()
    {
        var sut = CreateWithPages(3);
        var firstPage = sut.Pages[0];

        sut.NextCommand.Execute(null);

        sut.ActivePageIndex.Should().Be(1);
        sut.ActivePage.Should().BeSameAs(sut.Pages[1]);
        firstPage.IsActive.Should().BeFalse();
        sut.Pages[1].IsActive.Should().BeTrue();
    }

    [Fact]
    public void PreviousCommand_moves_back_to_the_prior_page()
    {
        var sut = CreateWithPages(3);
        sut.NextCommand.Execute(null);

        sut.PreviousCommand.Execute(null);

        sut.ActivePageIndex.Should().Be(0);
        sut.ActivePage.Should().BeSameAs(sut.Pages[0]);
    }

    [Fact]
    public void VisitedPages_accumulates_across_navigation_without_duplicates()
    {
        var sut = CreateWithPages(3);

        sut.NextCommand.Execute(null); // -> page 1
        sut.NextCommand.Execute(null); // -> page 2
        sut.PreviousCommand.Execute(null); // -> page 1 again

        sut.VisitedPages.Should().HaveCount(3);
        sut.VisitedPages.Should().Contain(sut.Pages[0]);
        sut.VisitedPages.Should().Contain(sut.Pages[1]);
        sut.VisitedPages.Should().Contain(sut.Pages[2]);
    }

    [Fact]
    public void IsOnLastPage_is_true_only_on_the_final_page()
    {
        var sut = CreateWithPages(2);

        sut.IsOnLastPage.Should().BeFalse();

        sut.NextCommand.Execute(null);

        sut.IsOnLastPage.Should().BeTrue();
    }

    [Fact]
    public void Navigating_raises_PropertyChanged_for_ActivePage_ActivePageIndex_and_IsOnLastPage()
    {
        var sut = CreateWithPages(2);
        var raised = new List<string?>();
        sut.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        sut.NextCommand.Execute(null);

        raised.Should().Contain(nameof(WizardViewModel.ActivePage));
        raised.Should().Contain(nameof(WizardViewModel.ActivePageIndex));
        raised.Should().Contain(nameof(WizardViewModel.IsOnLastPage));
    }
}
