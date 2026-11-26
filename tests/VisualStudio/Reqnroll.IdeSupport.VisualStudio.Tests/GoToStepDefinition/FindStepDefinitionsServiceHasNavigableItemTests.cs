using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.Extension.FindUnusedStepDefinitions;
using Reqnroll.IdeSupport.VisualStudio.Extension.GoToStepDefinition;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.GoToStepDefinition;

/// <summary>
/// The client-side navigability test behind the Ctrl+hover underline
/// (<see cref="FindStepDefinitionsService.HasNavigableItem"/>): a binding whose source file is on
/// this machine, or one with no local source that carries a method identity Visual Studio can
/// navigate to, is somewhere Go To Definition can take the user.
/// </summary>
public class FindStepDefinitionsServiceHasNavigableItemTests
{
    private static StepDefinitionListItem Item(string? sourceFile, bool isResolved) =>
        new() { ClassName = "Steps", MethodName = "AStep", SourceFile = sourceFile, IsResolved = isResolved };

    [Fact]
    public void No_items_is_not_navigable() =>
        FindStepDefinitionsService.HasNavigableItem(Array.Empty<StepDefinitionListItem>()).Should().BeFalse();

    [Fact]
    public void A_resolved_item_with_a_source_file_is_navigable() =>
        FindStepDefinitionsService.HasNavigableItem(new[] { Item(@"C:\repo\Steps.cs", true) }).Should().BeTrue();

    [Fact]
    public void An_item_whose_source_is_not_on_this_machine_is_not_navigable() =>
        FindStepDefinitionsService.HasNavigableItem(new[] { Item(null, false) }).Should().BeFalse();

    [Fact]
    public void An_unresolved_item_with_a_method_identity_is_navigable()
    {
        var item = Item(null, false);
        item.MethodIdentity = new Reqnroll.IdeSupport.VisualStudio.Extension.Navigation.BindingMethodIdentity(
            "External, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "M:External.Steps.AStep");

        FindStepDefinitionsService.HasNavigableItem(new[] { item }).Should().BeTrue();
    }

    [Fact]
    public void One_navigable_item_among_unresolved_ones_is_enough() =>
        FindStepDefinitionsService.HasNavigableItem(new[] { Item(null, false), Item(@"C:\repo\Steps.cs", true) }).Should().BeTrue();
}
