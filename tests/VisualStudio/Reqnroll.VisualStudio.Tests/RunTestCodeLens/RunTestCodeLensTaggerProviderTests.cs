using AwesomeAssertions;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;
using Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;
using Xunit;

namespace Reqnroll.VisualStudio.Tests.RunTestCodeLens;

/// <summary>
/// Unit tests for <see cref="RunTestCodeLensTaggerProvider.EncodeElementDescription"/> — the
/// Run-CodeLens-specific revision-key formatting built on the shared
/// <see cref="LineElementDescription"/> envelope (issue #262 follow-up refactor). Mirrors
/// <c>HookElementDescriptionTests</c>'s shape for <c>HookCodeLensTaggerProvider</c>'s counterpart.
/// </summary>
/// <remarks>
/// The revision component is load-bearing rather than cosmetic: <c>LineKeyedCodeLensTagger{TEntry}</c>
/// reuses a tag instance for as long as its <c>ElementDescription</c> is unchanged, so if the
/// encoding failed to vary with a line's resolved-target content, a changed Run target would never
/// reach the editor.
/// </remarks>
public class RunTestCodeLensTaggerProviderTests
{
    private static RunTestTargetEntry Entry(
        int line = 1,
        string outputAssemblyPath = @"C:\bin\Tests.dll",
        string declaringTypeFullName = "Tests.FFeature",
        string methodName = "AddTwoNumbers") =>
        new(line, outputAssemblyPath, declaringTypeFullName, methodName);

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4_096)]
    public void Encode_then_TryDecode_recovers_the_line(int line)
    {
        var encoded = RunTestCodeLensTaggerProvider.EncodeElementDescription(line, new[] { Entry(line: line) });

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(line);
    }

    [Fact]
    public void Encode_of_multiple_targets_on_one_line_still_decodes_to_that_one_line()
    {
        // A parameterized Scenario Outline row-tests case: several targets share one line.
        var encoded = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            Entry(methodName: "AddNumbers"),
            Entry(methodName: "AddNumbers_2"),
        });

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(1);
    }

    // ── Revision component: must vary with content, else lenses never refresh ──

    [Fact]
    public void The_encoding_changes_when_the_method_name_changes()
    {
        var before = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(methodName: "AddTwoNumbers") });
        var after  = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(methodName: "AddThreeNumbers") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_changes_when_the_declaring_type_changes()
    {
        var before = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(declaringTypeFullName: "Tests.FFeature") });
        var after  = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(declaringTypeFullName: "Tests.GFeature") });

        after.Should().NotBe(before);
    }

    [Fact]
    public void The_encoding_changes_when_a_target_is_added_to_the_line()
    {
        var single = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(methodName: "AddNumbers") });
        var both   = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[]
        {
            Entry(methodName: "AddNumbers"),
            Entry(methodName: "AddNumbers_2"),
        });

        both.Should().NotBe(single);
    }

    [Fact]
    public void The_encoding_does_not_depend_on_the_output_assembly_path()
    {
        // OutputAssemblyPath is carried for VS Test Explorer's TestMethodIdentifier but isn't part
        // of what identifies *which* test a line resolves to — a rebuild that only moves the output
        // path must not spuriously invalidate an otherwise-unchanged tag.
        var before = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(outputAssemblyPath: @"C:\bin\Debug\Tests.dll") });
        var after  = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(outputAssemblyPath: @"C:\bin\Release\Tests.dll") });

        after.Should().Be(before);
    }

    [Fact]
    public void The_encoding_is_stable_for_unchanged_content()
    {
        var entries = new[] { Entry(methodName: "AddNumbers"), Entry(methodName: "AddNumbers_2") };

        RunTestCodeLensTaggerProvider.EncodeElementDescription(1, entries)
            .Should().Be(RunTestCodeLensTaggerProvider.EncodeElementDescription(1, entries));
    }

    [Fact]
    public void The_encoding_does_not_depend_on_the_order_entries_arrive_in()
    {
        var first  = Entry(declaringTypeFullName: "Tests.FFeature", methodName: "AddNumbers");
        var second = Entry(declaringTypeFullName: "Tests.FFeature", methodName: "AddNumbers_2");

        RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { first, second })
            .Should().Be(RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { second, first }));
    }

    [Fact]
    public void Two_different_lines_encode_differently_even_with_identical_target_content()
    {
        var line1 = RunTestCodeLensTaggerProvider.EncodeElementDescription(1, new[] { Entry(line: 1) });
        var line9 = RunTestCodeLensTaggerProvider.EncodeElementDescription(9, new[] { Entry(line: 9) });

        line9.Should().NotBe(line1);
    }

    [Fact]
    public void TryDecode_of_a_line_with_no_targets_still_yields_the_line()
    {
        var encoded = RunTestCodeLensTaggerProvider.EncodeElementDescription(3, System.Array.Empty<RunTestTargetEntry>());

        LineElementDescription.TryDecode(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(3);
    }
}
