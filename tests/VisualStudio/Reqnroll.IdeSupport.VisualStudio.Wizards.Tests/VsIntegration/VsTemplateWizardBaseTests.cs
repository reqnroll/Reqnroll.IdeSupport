using Microsoft.VisualStudio.TemplateWizard;

namespace Reqnroll.IdeSupport.VisualStudio.Wizards.Tests.VsIntegration;

/// <summary>
/// <see cref="VsTemplateWizardBase{TWizard}.RunStarted(object, Dictionary{string, string}, WizardRunKind, object[])"/>
/// is the VS <see cref="IWizard"/> entry point and is COM/DTE-bound past its first two guard
/// clauses, so these tests only exercise the parts reachable without a real DTE: the early-outs
/// for an unsupported <see cref="WizardRunKind"/> and for a non-DTE automation object, plus the
/// no-op <see cref="IWizard"/> hooks. <see cref="VsReqnrollConfigFileWizard"/> is used as the
/// concrete subclass since none of it overrides <c>RunStarted</c>/<c>RunFinished</c>.
/// </summary>
public class VsTemplateWizardBaseTests
{
    private static VsReqnrollConfigFileWizard CreateSut() => new();

    [Fact]
    public void RunStarted_ignores_the_unsupported_AsMultiProject_run_kind_without_touching_the_automation_object()
    {
        var sut = CreateSut();

        sut.RunStarted(automationObjectDte: null, new Dictionary<string, string>(),
            WizardRunKind.AsMultiProject, Array.Empty<object>());

        sut.ShouldAddProjectItem("Some.feature").Should().BeFalse();
    }

    [Theory]
    [InlineData(WizardRunKind.AsNewItem)]
    [InlineData(WizardRunKind.AsNewProject)]
    public void RunStarted_leaves_the_run_invalid_when_the_automation_object_is_not_a_DTE(WizardRunKind runKind)
    {
        var sut = CreateSut();

        sut.RunStarted(automationObjectDte: new object(), new Dictionary<string, string>(), runKind, Array.Empty<object>());

        sut.ShouldAddProjectItem("Some.feature").Should().BeFalse();
    }

    [Fact]
    public void ShouldAddProjectItem_returns_false_before_any_run_has_started()
    {
        CreateSut().ShouldAddProjectItem("Some.feature").Should().BeFalse();
    }

    [Fact]
    public void ProjectFinishedGenerating_is_a_no_op()
    {
        var act = () => CreateSut().ProjectFinishedGenerating(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void BeforeOpeningFile_is_a_no_op()
    {
        var act = () => CreateSut().BeforeOpeningFile(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ProjectItemFinishedGenerating_is_a_no_op_when_the_run_is_invalid()
    {
        // _isValidRun is false on a fresh instance, so this must return before touching
        // _wizardContext (which is still null at this point).
        var act = () => CreateSut().ProjectItemFinishedGenerating(null);

        act.Should().NotThrow();
    }

    [Fact]
    public void RunFinished_resets_state_and_is_safe_to_call_on_a_fresh_instance()
    {
        var sut = CreateSut();

        var act = () => sut.RunFinished();

        act.Should().NotThrow();
        sut.ShouldAddProjectItem("Some.feature").Should().BeFalse();
    }
}
