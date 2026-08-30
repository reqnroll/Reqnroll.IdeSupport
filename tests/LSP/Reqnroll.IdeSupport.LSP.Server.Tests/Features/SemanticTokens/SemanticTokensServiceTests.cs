using Reqnroll.IdeSupport.Common.Classification;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;


using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Position = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.SemanticTokens;

public class SemanticTokenServiceTests
{
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private SemanticTokensService CreateSut() => new(_bufferService, _logger);

    private void SetupBuffer(DocumentBuffer? buf)
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(x =>
        {
            x[1] = buf;
            return buf is not null;
        });
    }

    // ── Legend ────────────────────────────────────────────────────────────────

    [Fact]
    public void Legend_is_not_null()
    {
        var sut = CreateSut();
        sut.Legend.Should().NotBeNull();
    }

    [Fact]
    public void Legend_contains_the_custom_reqnroll_token_types()
    {
        var sut = CreateSut();
        var advertised = sut.Legend.TokenTypes.Select(t => t.ToString()).ToList();
        advertised.Should().Contain(ReqnrollClassificationTypeNames.Keyword);
        advertised.Should().Contain(ReqnrollClassificationTypeNames.StepParameter);
        advertised.Should().Contain(ReqnrollClassificationTypeNames.UndefinedStep);
    }

    [Fact]
    public void Legend_declares_no_token_modifiers()
    {
        var sut = CreateSut();
        sut.Legend.TokenModifiers.Should().BeEmpty();
    }

    // ── No buffer / no tags ───────────────────────────────────────────────────

    [Fact]
    public async Task GetSemanticTokensAsync_returns_null_when_buffer_not_registered()
    {
        SetupBuffer(null);
        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 1);
        result.Should().NotBeNull();
        result!.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSemanticTokensAsync_returns_null_when_buffer_has_no_tags()
    {
        var buf = new DocumentBuffer(FeatureUri, 1, "Feature: X\n"); // Tags is null/empty
        SetupBuffer(buf);
        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 1);
        result.Should().NotBeNull();
        result!.Data.Should().BeEmpty();
    }

    // ── With tags ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSemanticTokensAsync_returns_non_null_when_tags_available()
    {
        // Build a buffer with at least one tag that maps to a token type.
        // We need a real IdeSupportTag with a valid GherkinRange; use a minimal
        // stub snapshot so the range/position calculation works.
        var snapshot = new TestGherkinSnapshot("Feature: Test\n  Scenario: S\n    Given something\n");
        var range = new GherkinRange(snapshot, 0, snapshot.Length);
        var tag = new IdeSupportTag(IdeSupportTagTypes.DefinitionLineKeyword, range);

        var buf = new DocumentBuffer(FeatureUri, 2, snapshot.GetText());
        buf = buf with { Tags = new[] { tag } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 2);
        result.Should().NotBeNull();
        result!.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetSemanticTokensAsync_returns_cached_result_on_second_call()
    {
        var snapshot = new TestGherkinSnapshot("Feature: Test\n");
        var range = new GherkinRange(snapshot, 0, 7);
        var tag = new IdeSupportTag(IdeSupportTagTypes.StepKeyword, range);

        var buf = new DocumentBuffer(FeatureUri, 3, snapshot.GetText());
        buf = buf with { Tags = new[] { tag } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var first = await sut.GetSemanticTokensAsync(FeatureUri, 3);
        var second = await sut.GetSemanticTokensAsync(FeatureUri, 3);

        second.Should().BeSameAs(first);
        // Buffer service should only be called once (second call hits cache)
        _bufferService.Received(1).TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>());
    }

    // ── Multi-line DataTable tag ──────────────────────────────────────────────

    [Fact]
    public async Task GetSemanticTokensAsync_emits_a_DataTable_token_for_every_row_of_a_multiline_table()
    {
        // Mirrors IdeSupportTagParser's real output shape for a multi-row table: one DataTable
        // block tag spanning the whole table (header row through the last row), with
        // DataTableHeader cell tags nested only on the header row -- data rows have no
        // per-cell tags of their own (see IdeSupportTagParser.TagRowCells, only ever called for
        // the header row), so each data row must surface as its own whole-line DataTable
        // token via Encode's multi-line "middle/last line" handling, not be silently dropped.
        var text =
            "Feature: F\n" +
            "Scenario: S\n" +
            "  Given a step\n" +
            "    | col1 | col2 |\n" +  // line 3 -- header
            "    | a    | b    |\n" +  // line 4 -- data row
            "    | c    | d    |\n";   // line 5 -- data row
        var snapshot = new TestGherkinSnapshot(text);

        int tableStart = text.IndexOf("| col1", StringComparison.Ordinal);
        int tableEnd = text.IndexOf("| d    |", StringComparison.Ordinal) + "| d    |".Length;
        var tableTag = new IdeSupportTag(IdeSupportTagTypes.DataTable, new GherkinRange(snapshot, tableStart, tableEnd - tableStart));

        int header1Start = text.IndexOf("col1", StringComparison.Ordinal);
        var header1 = new IdeSupportTag(IdeSupportTagTypes.DataTableHeader,
            new GherkinRange(snapshot, header1Start, "col1".Length));
        int header2Start = text.IndexOf("col2", StringComparison.Ordinal);
        var header2 = new IdeSupportTag(IdeSupportTagTypes.DataTableHeader,
            new GherkinRange(snapshot, header2Start, "col2".Length));

        var buf = new DocumentBuffer(FeatureUri, 5, text) with { Tags = new[] { tableTag, header1, header2 } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var result = await sut.GetSemanticTokensAsync(FeatureUri, 5);
        var tokens = Decode(result!.Data.ToArray());

        ReqnrollSemanticTokens.TryGetToken(tableTag, out var dataTableType, out _);
        ReqnrollSemanticTokens.TryGetToken(header1, out var headerType, out _);

        tokens.Should().Contain(t => t.Line == 3 && t.Char == 6 && t.Length == 4 && t.Type == headerType, "\"col1\" gets its own DataTableHeader token");
        tokens.Should().Contain(t => t.Line == 3 && t.Char == 13 && t.Length == 4 && t.Type == headerType, "\"col2\" gets its own DataTableHeader token");
        tokens.Should().Contain(t => t.Line == 4 && t.Type == dataTableType && t.Length == 19, "a data row must not be silently dropped");
        tokens.Should().Contain(t => t.Line == 5 && t.Type == dataTableType && t.Length == 19, "the last data row must not be silently dropped either");
    }

    // ── Range-scoped semantic tokens ───────────────────────────────────────────

    [Fact]
    public async Task GetSemanticTokensForRangeAsync_excludes_tags_outside_the_requested_line_range()
    {
        // "Given x" appears on line 2 and again on line 20 of a repeated Scenario block.
        var text = "Feature: F\n" + string.Concat(Enumerable.Repeat("  Scenario: S\n    Given x\n", 10));
        var snapshot = new TestGherkinSnapshot(text);
        var firstOffset = text.IndexOf("Given x", StringComparison.Ordinal);
        var lastOffset  = text.LastIndexOf("Given x", StringComparison.Ordinal);

        var tag1 = new IdeSupportTag(IdeSupportTagTypes.DefinitionLineKeyword, new GherkinRange(snapshot, firstOffset, 7));
        var tag2 = new IdeSupportTag(IdeSupportTagTypes.DefinitionLineKeyword, new GherkinRange(snapshot, lastOffset, 7));

        var buf = new DocumentBuffer(FeatureUri, 1, snapshot.GetText()) with { Tags = new[] { tag1, tag2 } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var range = new LspRange(new Position(0, 0), new Position(3, 0)); // covers only the first "Given x" (line 2)

        var result = await sut.GetSemanticTokensForRangeAsync(FeatureUri, 1, range, CancellationToken.None);

        // 5 ints per token (deltaLine, deltaChar, length, type, modifiers) -- only one of the two tags qualifies.
        result!.Data.Length.Should().Be(5);
        // ...and it is specifically the in-range one (line 2), not the line-20 tag.
        Decode(result.Data.ToArray()).Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public async Task GetSemanticTokensForRangeAsync_result_id_is_distinguishable_from_the_full_document_one()
    {
        // A range result is a strict subset of the full-document result, so reusing the
        // full-document ResultId would let a later semanticTokens/full/delta request diff
        // against the wrong baseline (issue #471 final review).
        var text = "Feature: F\n" + string.Concat(Enumerable.Repeat("  Scenario: S\n    Given x\n", 10));
        var snapshot = new TestGherkinSnapshot(text);
        var offset = text.IndexOf("Given x", StringComparison.Ordinal);
        var tag = new IdeSupportTag(IdeSupportTagTypes.DefinitionLineKeyword, new GherkinRange(snapshot, offset, 7));

        var buf = new DocumentBuffer(FeatureUri, 1, snapshot.GetText()) with { Tags = new[] { tag } };
        SetupBuffer(buf);

        var sut = CreateSut();
        var full  = await sut.GetSemanticTokensAsync(FeatureUri, 1);
        var ranged = await sut.GetSemanticTokensForRangeAsync(
            FeatureUri, 1, new LspRange(new Position(0, 0), new Position(3, 0)), CancellationToken.None);

        ranged!.ResultId.Should().NotBe(full!.ResultId);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static List<(int Line, int Char, int Length, int Type)> Decode(int[] data)
    {
        var result = new List<(int, int, int, int)>();
        int line = 0, ch = 0;
        for (int i = 0; i < data.Length; i += 5)
        {
            if (data[i] != 0) { line += data[i]; ch = data[i + 1]; }
            else { ch += data[i + 1]; }
            result.Add((line, ch, data[i + 2], data[i + 3]));
        }
        return result;
    }


    /// <summary>Minimal IGherkinTextSnapshot backed by a plain string.</summary>
    private sealed class TestGherkinSnapshot : Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshot
    {
        private readonly string _text;
        private readonly string[] _lines;

        public TestGherkinSnapshot(string text)
        {
            _text = text;
            // Split preserving line content; last empty segment from trailing \n is dropped.
            _lines = text.Split('\n');
        }

        public int Version => 1;
        public int Length => _text.Length;
        public int LineCount => _lines.Length;
        public string GetText() => _text;

        public Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshotLine GetLineFromLineNumber(int lineNumber)
            => new Line(_lines, lineNumber);

        private sealed class Line : Reqnroll.IdeSupport.LSP.Core.Documents.IGherkinTextSnapshotLine
        {
            private readonly string[] _lines;
            private readonly int _lineNumber;
            private readonly int _start;

            public Line(string[] lines, int lineNumber)
            {
                _lines = lines;
                _lineNumber = lineNumber;
                int s = 0;
                for (int i = 0; i < lineNumber; i++)
                    s += lines[i].Length + 1; // +1 for \n
                _start = s;
            }

            public int LineNumber => _lineNumber;
            public int Start => _start;
            public int End => _start + _lines[_lineNumber].Length;
            public string GetText() => _lines[_lineNumber];
        }
    }
}
