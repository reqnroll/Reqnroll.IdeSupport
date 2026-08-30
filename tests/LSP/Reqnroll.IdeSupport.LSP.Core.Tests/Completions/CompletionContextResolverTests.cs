using Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Completions;
using GherkinLocation = Gherkin.Ast.Location;






namespace Reqnroll.IdeSupport.LSP.Core.Tests.Completions;

/// <summary>
/// Unit tests for <see cref="CompletionContextResolver"/>.
/// The tag parser and monitoring service are mocked; a lightweight <see cref="FakeSnapshot"/>
/// stands in for the Server-layer LspTextSnapshot.
///
/// All offset/column arithmetic is validated against this fixed document layout (lines 0-based):
///   Line 0: "Feature: F"            Start= 0  End=10  length=10
///   Line 1: "Scenario: S"           Start=11  End=22  length=11
///   Line 2: "    Given some text"   Start=23  End=42  length=19
///   Line 3: "    When I do it"      Start=43  End=59  length=16
///
/// Step on line 2: Location.Column=5 (1-based), keyword="Given " (6 chars)
///   → stepTextStartColumn = (5-1) + 6 = 10
///   → stepTextStart (absolute) = 23 + 10 = 33  ("s" of "some")
/// </summary>
public class CompletionContextResolverTests
{
    private static readonly string DocText =
        "Feature: F\nScenario: S\n    Given some text\n    When I do it";

    // Step placed on line 2 (0-based).  Gherkin Location.Line is 1-based, so Line=3.
    private static readonly IdeSupportGherkinStep StepLine2 =
        new(new GherkinLocation(3, 5), "Given ", StepKeywordType.Context,
            "some text", null!, StepKeyword.Given, ScenarioBlock.Given);

    // Step placed on line 3 (0-based) — used for "different line" tests.
    private static readonly IdeSupportGherkinStep StepLine3 =
        new(new GherkinLocation(4, 5), "When ", StepKeywordType.Action,
            "I do it", null!, StepKeyword.When, ScenarioBlock.When);

    private readonly IIdeSupportTagParser _tagParser  = Substitute.For<IIdeSupportTagParser>();
    private readonly ITelemetryService _monitoring = Substitute.For<ITelemetryService>();
    private readonly CompletionContextResolver _sut;

    public CompletionContextResolverTests()
    {
        _sut = new CompletionContextResolver(_tagParser, _monitoring);
    }

    // ── No step at cursor → keyword context ──────────────────────────────────

    [Fact]
    public void No_step_tags_returns_KeywordCompletionContext()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>()).Returns(Array.Empty<IdeSupportTag>());

        var ctx = _sut.Resolve(snapshot, 0, 0, ProjectBindingRegistry.Invalid, "en");

        ctx.Should().BeOfType<KeywordCompletionContext>();
    }

    [Fact]
    public void Step_tag_on_different_line_returns_KeywordCompletionContext()
    {
        var snapshot = Snapshot(DocText);
        // StepLine3 is on line 3 (0-based); cursor is on line 2.
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine3, snapshot) });

        var ctx = _sut.Resolve(snapshot, 2, 0, ProjectBindingRegistry.Invalid, "en");

        ctx.Should().BeOfType<KeywordCompletionContext>();
    }

    // ── Cursor before step text start → keyword context ──────────────────────

    [Fact]
    public void Cursor_before_step_text_start_returns_KeywordCompletionContext()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        // stepTextStartColumn = 10; cursor at col 9 is still in the keyword.
        var ctx = _sut.Resolve(snapshot, 2, 9, ProjectBindingRegistry.Invalid, "en");

        ctx.Should().BeOfType<KeywordCompletionContext>();
    }

    [Fact]
    public void Cursor_at_col_zero_on_step_line_returns_KeywordCompletionContext()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        var ctx = _sut.Resolve(snapshot, 2, 0, ProjectBindingRegistry.Invalid, "en");

        ctx.Should().BeOfType<KeywordCompletionContext>();
    }

    // ── Cursor at/past step text start → step context ─────────────────────────

    [Fact]
    public void Cursor_at_exactly_step_text_start_returns_StepCompletionContext()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        // col 10 = first char of step text ("s" of "some")
        var ctx = _sut.Resolve(snapshot, 2, 10, ProjectBindingRegistry.Invalid, "en");

        ctx.Should().BeOfType<StepCompletionContext>();
    }

    [Fact]
    public void Cursor_past_step_text_start_returns_StepCompletionContext()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        var ctx = _sut.Resolve(snapshot, 2, 14, ProjectBindingRegistry.Invalid, "en");

        ctx.Should().BeOfType<StepCompletionContext>();
    }

    // ── TypedAfterKeyword ─────────────────────────────────────────────────────

    [Fact]
    public void TypedAfterKeyword_is_empty_when_cursor_at_step_text_start()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        var ctx = (StepCompletionContext)_sut.Resolve(snapshot, 2, 10, ProjectBindingRegistry.Invalid, "en")!;

        ctx.TypedAfterKeyword.Should().BeEmpty();
    }

    [Fact]
    public void TypedAfterKeyword_contains_text_typed_after_keyword()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        // "    Given some text" — col 10 starts "some text"; col 14 is after "some"
        var ctx = (StepCompletionContext)_sut.Resolve(snapshot, 2, 14, ProjectBindingRegistry.Invalid, "en")!;

        ctx.TypedAfterKeyword.Should().Be("some");
    }

    // ── StepTextStartColumn ───────────────────────────────────────────────────

    [Fact]
    public void StepTextStartColumn_equals_indent_plus_keyword_length()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot) });

        var ctx = (StepCompletionContext)_sut.Resolve(snapshot, 2, 10, ProjectBindingRegistry.Invalid, "en")!;

        // Location.Column=5 (1-based indent=4), keyword="Given " (length=6): 4+6=10
        ctx.StepTextStartColumn.Should().Be(10);
    }

    [Fact]
    public void StepTextStartColumn_reflects_keyword_length_difference()
    {
        // "When " is 5 chars vs "Given " which is 6 — column stays same, keyword shorter.
        // Location.Column=5 (1-based), keyword="When " (5 chars): (5-1)+5=9
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine3, snapshot) });

        var ctx = (StepCompletionContext)_sut.Resolve(snapshot, 3, 9, ProjectBindingRegistry.Invalid, "en")!;

        ctx.StepTextStartColumn.Should().Be(9);
    }

    // ── Dialect resolution ────────────────────────────────────────────────────

    [Fact]
    public void Fallback_language_used_when_no_document_tag()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>()).Returns(Array.Empty<IdeSupportTag>());

        var ctx = (KeywordCompletionContext)_sut.Resolve(snapshot, 0, 0, ProjectBindingRegistry.Invalid, "de")!;

        ctx.Dialect.Should().NotBeNull();
        ctx.Dialect.Language.Should().Be("de");
    }

    [Fact]
    public void Parsed_document_dialect_takes_priority_over_fallback_language()
    {
        var deDialect = new GherkinDialectProvider("de").DefaultDialect;
        var deDoc     = new IdeSupportGherkinDocument(
            null!, Enumerable.Empty<global::Gherkin.Ast.Comment>(), "",
            deDialect, new List<int>());

        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { DocTag(deDoc, snapshot) });

        // Fallback says "en" but the parsed doc says "de" — parsed wins.
        var ctx = (KeywordCompletionContext)_sut.Resolve(snapshot, 0, 0, ProjectBindingRegistry.Invalid, "en")!;

        ctx.Dialect.Language.Should().Be("de");
    }

    // ── StepCompletionContext carries the step object ─────────────────────────

    [Fact]
    public void StepCompletionContext_carries_the_step_for_the_cursor_line()
    {
        var snapshot = Snapshot(DocText);
        _tagParser.Parse(snapshot, Arg.Any<ProjectBindingRegistry>())
            .Returns(new[] { StepTag(StepLine2, snapshot), StepTag(StepLine3, snapshot) });

        var ctx = (StepCompletionContext)_sut.Resolve(snapshot, 2, 10, ProjectBindingRegistry.Invalid, "en")!;

        ctx.Step.Should().BeSameAs(StepLine2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IGherkinTextSnapshot Snapshot(string text) => new FakeSnapshot(text);

    private static IdeSupportTag StepTag(IdeSupportGherkinStep step, IGherkinTextSnapshot snapshot)
        => new(IdeSupportTagTypes.StepBlock, GherkinRange.Empty, step);

    private static IdeSupportTag DocTag(IdeSupportGherkinDocument doc, IGherkinTextSnapshot snapshot)
        => new(IdeSupportTagTypes.Document, GherkinRange.Empty, doc);

    // ── Inline snapshot (avoids a dependency on the Server layer) ─────────────

    private sealed class FakeSnapshot : IGherkinTextSnapshot
    {
        private readonly string _text;
        private readonly IReadOnlyList<FakeLine> _lines;

        public FakeSnapshot(string text)
        {
            _text = text;
            var lines = new List<FakeLine>();
            int start = 0, num = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines.Add(new FakeLine(num++, start, i, text));
                    start = i + 1;
                }
            }
            lines.Add(new FakeLine(num, start, text.Length, text));
            _lines = lines;
        }

        public int    Version   => 1;
        public int    LineCount => _lines.Count;
        public int    Length    => _text.Length;
        public string GetText() => _text;

        public IGherkinTextSnapshotLine GetLineFromLineNumber(int n)
            => _lines[Math.Min(n, _lines.Count - 1)];
    }

    private sealed class FakeLine : IGherkinTextSnapshotLine
    {
        private readonly string _text;

        public FakeLine(int lineNumber, int start, int end, string text)
        {
            LineNumber = lineNumber;
            Start      = start;
            End        = end;
            _text      = text;
        }

        public int    LineNumber { get; }
        public int    Start      { get; }
        public int    End        { get; }
        public string GetText()  => _text.Substring(Start, End - Start);
    }
}
