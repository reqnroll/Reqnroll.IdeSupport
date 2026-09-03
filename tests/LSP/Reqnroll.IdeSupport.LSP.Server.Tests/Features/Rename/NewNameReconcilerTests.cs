using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

/// <summary>
/// Issue #568: <see cref="NewNameReconciler.Reconcile"/> is only exercised indirectly through
/// <c>StepRenameHandlerTests</c>' happy-path scenarios — the <c>oldStepText == null</c> fallback
/// and the "reject rename because parameter values changed" branch have no test at any level.
/// These tests exercise <see cref="NewNameReconciler"/> directly.
/// </summary>
public class NewNameReconcilerTests
{
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private NewNameReconciler CreateSut() => new(_logger);

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/x.feature");

    private static StepBindingMatch MakeUsage(string featureUri, string stepText)
    {
        var text = $"Feature: F\nScenario: S\n\tGiven {stepText}\n";
        var snapshot = new LspTextSnapshot(featureUri, 1, text);
        var startOffset = text.IndexOf(stepText, StringComparison.Ordinal);
        var range = GherkinRange.FromPoint(snapshot, startOffset, stepText.Length);
        return new StepBindingMatch(featureUri, range, MatchResult.NoMatch);
    }

    [Fact]
    public void Reconcile_returns_newName_unchanged_for_a_cs_file_regardless_of_slot_count()
    {
        var sut = CreateSut();

        var result = sut.Reconcile(
            path: "/workspace/Steps.cs",
            uri: DocumentUri.FromFileSystemPath("/workspace/Steps.cs"),
            position: new Position(0, 0),
            usages: Array.Empty<StepBindingMatch>(),
            sourceExpression: "the first number is {int}",
            newName: "the first number is a totally different concrete value",
            readStepText: (_, _) => null);

        result.Should().Be("the first number is a totally different concrete value");
    }

    [Fact]
    public void Reconcile_returns_newName_unchanged_when_parameter_slot_counts_match()
    {
        var sut = CreateSut();

        var result = sut.Reconcile(
            path: "/workspace/x.feature",
            uri: FeatureUri,
            position: new Position(2, 8),
            usages: Array.Empty<StepBindingMatch>(),
            sourceExpression: "the first number is {int}",
            newName: "the first no is {int}",
            readStepText: (_, _) => throw new InvalidOperationException("should not be called — slot counts already match"));

        result.Should().Be("the first no is {int}");
    }

    [Fact]
    public void Reconcile_falls_back_to_newName_when_no_usage_covers_the_edited_position()
    {
        var sut = CreateSut();
        var usage = MakeUsage(FeatureUri.ToString(), "the first number is 10");

        var result = sut.Reconcile(
            path: "/workspace/x.feature",
            uri: FeatureUri,
            position: new Position(99, 0), // no usage covers this line
            usages: new[] { usage },
            sourceExpression: "the first number is {int}",
            newName: "the first number is different wording entirely",
            readStepText: (_, _) => throw new InvalidOperationException("should not be called — no usage at this position"));

        result.Should().Be("the first number is different wording entirely");
    }

    [Fact]
    public void Reconcile_falls_back_to_newName_when_the_original_step_text_cannot_be_read()
    {
        var sut = CreateSut();
        var usage = MakeUsage(FeatureUri.ToString(), "the first number is 10");

        var result = sut.Reconcile(
            path: "/workspace/x.feature",
            uri: FeatureUri,
            position: new Position(2, 8),
            usages: new[] { usage },
            sourceExpression: "the first number is {int}",
            newName: "the first number is different wording entirely",
            readStepText: (_, _) => null); // buffer and disk both unavailable

        result.Should().Be("the first number is different wording entirely",
            "with no original text to diff against, the edited name is used as-is");
    }

    [Fact]
    public void Reconcile_derives_the_abstract_expression_from_the_edited_concrete_step_text()
    {
        var sut = CreateSut();
        var usage = MakeUsage(FeatureUri.ToString(), "the first number is 10");

        var result = sut.Reconcile(
            path: "/workspace/x.feature",
            uri: FeatureUri,
            position: new Position(2, 8),
            usages: new[] { usage },
            sourceExpression: "the first number is {int}",
            newName: "the first no is 10",
            readStepText: (_, _) => "the first number is 10");

        result.Should().Be("the first no is {int}",
            "the {int} parameter slot should be preserved around the user's re-worded wording");
    }

    [Fact]
    public void Reconcile_returns_null_when_the_parameter_value_appears_to_have_changed()
    {
        var sut = CreateSut();
        var usage = MakeUsage(FeatureUri.ToString(), "the first number is 10");

        var result = sut.Reconcile(
            path: "/workspace/x.feature",
            uri: FeatureUri,
            position: new Position(2, 8),
            usages: new[] { usage },
            sourceExpression: "the first number is {int}",
            newName: "the first number is completely different",
            readStepText: (_, _) => "the first number is 10");

        result.Should().BeNull(
            "the captured parameter value '10' no longer appears verbatim in the edited text, so the rename must be rejected rather than silently dropping the value");
    }

    [Fact]
    public void Reconcile_matches_a_usage_by_line_alone_regardless_of_character_position()
    {
        var sut = CreateSut();
        var usage = MakeUsage(FeatureUri.ToString(), "the first number is 10");

        // position.Character (0, before the step text's own range even starts) is irrelevant to
        // the match — only position.Line falling within [Range.Start.Line, Range.End.Line] matters,
        // so this must still find the same usage the exact-character test above does.
        var result = sut.Reconcile(
            path: "/workspace/x.feature",
            uri: FeatureUri,
            position: new Position(2, 0),
            usages: new[] { usage },
            sourceExpression: "the first number is {int}",
            newName: "the first no is 10",
            readStepText: (_, _) => "the first number is 10");

        result.Should().Be("the first no is {int}");
    }
}
