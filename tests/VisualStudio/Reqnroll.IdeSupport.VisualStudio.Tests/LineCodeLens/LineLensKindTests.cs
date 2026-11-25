using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.VisualStudio.Language.CodeLens;
using Microsoft.VisualStudio.Language.CodeLens.Remoting;
using NSubstitute;
using Reqnroll.IdeSupport.VisualStudio.HookCodeLens;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Reqnroll.IdeSupport.VisualStudio.Tests.RunTestCodeLens;
using Xunit;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.LineCodeLens;

/// <summary>
/// Tests for the lens-kind component of <see cref="LineElementDescription"/> and the data-point
/// providers that decide on it. The classic CodeLens host offers every tag on a line to every
/// provider; a provider that accepts a tag whose line has no lens of its kind still gets an indicator
/// slot, which renders as an empty entry ("▶ Run Scenario |  |").
/// </summary>
[Collection(RunTestCodeLensRevisionCollection.Name)]
public class LineLensKindTests
{
    private static HookFeatureLensEntry HookEntry(bool stepHooks) =>
        new(1, stepHooks ? "2 step hooks" : "1 hook", 1, 0, OwnLevelOnly: !stepHooks, AlwaysShowPicker: stepHooks);

    private static FeatureLineLensEntry RunEntry(int line = 1) =>
        RunLensTagEntries.ToTagEntry(new RunTestLensLocation(line, "Scenario|AddTwoNumbers"));

    private static string RunDescription(int line = 1) =>
        FeatureCodeLensTaggerProvider.EncodeElementDescription(line, new[] { RunEntry(line) });

    private static string HookDescription(IEnumerable<HookFeatureLensEntry> entries) =>
        FeatureCodeLensTaggerProvider.EncodeElementDescription(1, entries.Select(HookLensTagEntries.ToTagEntry));

    private static CodeLensDescriptor Descriptor(string elementDescription) =>
        new() { FilePath = @"C:\repo\Calculator.feature", ElementDescription = elementDescription };

    private static Task<bool> HooksProviderAccepts(string elementDescription) =>
        new HookCodeLensDataPointProvider(Substitute.For<ICodeLensCallbackService>())
            .CanCreateDataPointAsync(Descriptor(elementDescription), null!, CancellationToken.None);

    private static Task<bool> StepHooksProviderAccepts(string elementDescription) =>
        new StepHooksCodeLensDataPointProvider(Substitute.For<ICodeLensCallbackService>())
            .CanCreateDataPointAsync(Descriptor(elementDescription), null!, CancellationToken.None);

    // ── Envelope ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_Run_tag_lists_only_the_Run_kind()
    {
        var description = RunDescription();

        LineElementDescription.HasLensKind(description, LineLensKinds.Run).Should().BeTrue();
        LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeFalse();
        LineElementDescription.HasLensKind(description, LineLensKinds.StepHooks).Should().BeFalse();
    }

    [Fact]
    public void A_Run_tag_key_containing_the_separator_does_not_leak_into_the_kinds()
    {
        // RunTestLensLocation keys look like "Scenario|Name", so only the first two separators may
        // be treated as structural.
        var description = FeatureCodeLensTaggerProvider.EncodeElementDescription(1, new[] { RunLensTagEntries.ToTagEntry(new RunTestLensLocation(1, "hooks|step-hooks")) });

        LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeFalse();
        LineElementDescription.HasLensKind(description, LineLensKinds.StepHooks).Should().BeFalse();
        LineElementDescription.TryDecode(description, out var line).Should().BeTrue();
        line.Should().Be(1);
    }

    [Fact]
    public void A_hook_tag_with_only_an_own_level_lens_lists_only_the_hooks_kind()
    {
        var description = HookDescription(new[] { HookEntry(stepHooks: false) });

        LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeTrue();
        LineElementDescription.HasLensKind(description, LineLensKinds.StepHooks).Should().BeFalse();
        LineElementDescription.HasLensKind(description, LineLensKinds.Run).Should().BeFalse();
    }

    [Fact]
    public void A_hook_tag_with_both_lenses_lists_both_kinds()
    {
        var description = HookDescription(new[] { HookEntry(stepHooks: true), HookEntry(stepHooks: false) });

        LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeTrue();
        LineElementDescription.HasLensKind(description, LineLensKinds.StepHooks).Should().BeTrue();
    }

    [Fact]
    public void Kinds_are_listed_in_a_stable_order_regardless_of_entry_order()
    {
        // Tag reuse compares descriptions, so a reordered server response must not churn the tag.
        var a = HookDescription(new[] { HookEntry(stepHooks: true), HookEntry(stepHooks: false) });
        var b = HookDescription(new[] { HookEntry(stepHooks: false), HookEntry(stepHooks: true) });

        a.Should().Be(b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1|")]
    [InlineData("1|hooks")]
    [InlineData("x|hooks|rev")]
    [InlineData("1||rev")]
    [InlineData("1|hooksx|rev")]
    public void HasLensKind_rejects_descriptions_that_do_not_list_the_kind(string? description)
    {
        LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeFalse();
    }

    [Fact]
    public void A_line_with_Run_and_hook_lenses_lists_every_kind_in_its_one_descriptor()
    {
        // VS keeps one tag per line and offers it to every provider, so the Run lens and both hook
        // lenses on a Scenario: line must all be described by the same descriptor.
        var description = FeatureCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            RunEntry(),
            HookLensTagEntries.ToTagEntry(HookEntry(stepHooks: false)),
            HookLensTagEntries.ToTagEntry(HookEntry(stepHooks: true)),
        });

        LineElementDescription.HasLensKind(description, LineLensKinds.Run).Should().BeTrue();
        LineElementDescription.HasLensKind(description, LineLensKinds.Hooks).Should().BeTrue();
        LineElementDescription.HasLensKind(description, LineLensKinds.StepHooks).Should().BeTrue();
    }

    [Fact]
    public void A_line_descriptor_changes_when_a_hook_lens_is_added_next_to_the_Run_lens()
    {
        var runOnly = FeatureCodeLensTaggerProvider.EncodeElementDescription(1, new[] { RunEntry() });
        var withHook = FeatureCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            RunEntry(), HookLensTagEntries.ToTagEntry(HookEntry(stepHooks: false)),
        });

        withHook.Should().NotBe(runOnly);
    }

    // ── Providers ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hook_providers_decline_a_Run_tag()
    {
        // The reported case: a Scenario line with no hooks has only the Run tag, and both hook
        // providers used to accept it and render an empty entry each.
        var description = RunDescription();

        (await HooksProviderAccepts(description)).Should().BeFalse();
        (await StepHooksProviderAccepts(description)).Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_own_level_provider_accepts_a_tag_with_only_an_own_level_lens()
    {
        var description = HookDescription(new[] { HookEntry(stepHooks: false) });

        (await HooksProviderAccepts(description)).Should().BeTrue();
        (await StepHooksProviderAccepts(description)).Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_step_hooks_provider_accepts_a_tag_with_only_a_step_hooks_lens()
    {
        var description = HookDescription(new[] { HookEntry(stepHooks: true) });

        (await HooksProviderAccepts(description)).Should().BeFalse();
        (await StepHooksProviderAccepts(description)).Should().BeTrue();
    }

    [Fact]
    public async Task Both_hook_providers_accept_a_Scenario_line_carrying_Run_and_both_hook_lenses()
    {
        // The regression a per-feature tag caused: VS kept the Run tag for the line, so a hook
        // provider that only trusted its own tagger's kinds never rendered.
        var description = FeatureCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            RunEntry(),
            HookLensTagEntries.ToTagEntry(HookEntry(stepHooks: false)),
            HookLensTagEntries.ToTagEntry(HookEntry(stepHooks: true)),
        });

        (await HooksProviderAccepts(description)).Should().BeTrue();
        (await StepHooksProviderAccepts(description)).Should().BeTrue();
    }

    [Fact]
    public async Task Both_hook_providers_accept_a_tag_with_both_lenses()
    {
        var description = HookDescription(new[] { HookEntry(stepHooks: false), HookEntry(stepHooks: true) });

        (await HooksProviderAccepts(description)).Should().BeTrue();
        (await StepHooksProviderAccepts(description)).Should().BeTrue();
    }
}
