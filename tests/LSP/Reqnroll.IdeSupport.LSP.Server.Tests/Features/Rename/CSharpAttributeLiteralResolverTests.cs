using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Documents;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

public class CSharpAttributeLiteralResolverTests
{
    // ── ReconcileParameterTokens unit behaviour ─────────────────────────────────────────

    [Theory]
    // regex-form edit over a Cucumber source → Cucumber type retained
    [InlineData("the first number is {int}", "the first no is (.*)", "the first no is {int}")]
    // Cucumber-form edit over a Cucumber source → verbatim
    [InlineData("the first number is {int}", "the first no is {int}", "the first no is {int}")]
    // regex source stays regex
    [InlineData("the first number is (.*)", "the first no is (.*)", "the first no is (.*)")]
    // multiple params, mixed forms → each slot takes the source token positionally
    [InlineData("a {int} b {string}", "x (.*) y (.*)", "x {int} y {string}")]
    // no parameters → verbatim
    [InlineData("just text", "renamed text", "renamed text")]
    // slot-count mismatch → user text honoured verbatim
    [InlineData("a {int}", "a {int} {word}", "a {int} {word}")]
    public void ReconcileParameterTokens_preserves_original_slot_tokens(
        string source, string newName, string expected)
    {
        CSharpAttributeLiteralResolver.ReconcileParameterTokens(source, newName).Should().Be(expected);
    }

    // ── FindAttributeLiteralAsync: method-name-style bindings (issue #344 follow-up) ────

    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

    private static CSharpAttributeLiteralResolver CreateSut(string csText)
    {
        var documentBuffer = Substitute.For<IDocumentBufferService>();
        documentBuffer
            .TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>())
            .Returns(ci =>
            {
                ci[1] = new DocumentBuffer(CsUri, 1, csText);
                return true;
            });

        return new CSharpAttributeLiteralResolver(
            new CSharpFileTextCache(),
            documentBuffer,
            Substitute.For<IIdeSupportLogger>(),
            new FileSystemForIDE());
    }

    [Fact]
    public async Task FindAttributeLiteralAsync_returns_null_for_a_method_name_style_binding_instead_of_a_nearby_methods_literal()
    {
        // Regression: a bare [Given] with no explicit expression (method-name-style, issue #344)
        // has no string-literal attribute argument anywhere in the file, by definition. Before this
        // fix, the resolver's "nearest candidate method" fallback had nothing of this binding's own
        // to find and silently snapped to a geometrically nearby method that DOES carry a literal —
        // misattributing that unrelated method's expression to this binding's rename (confirmed live
        // in VS: renaming a lone "the first number is 50" step offered "the second number is {int}"
        // from an unrelated nearby method as the placeholder).
        const string csText =
            "using Reqnroll;\n" +                                        // line 1
            "namespace N\n" +                                            // 2
            "{\n" +                                                     // 3
            "    [Binding]\n" +                                          // 4
            "    public class Steps\n" +                                 // 5
            "    {\n" +                                                 // 6
            "        [Given]\n" +                                       // 7 (0-based line 6)
            "        public void The_First_Number_Is_P0(int p0) { }\n" + // 8
            "\n" +                                                       // 9
            "        [Given(\"the second number is {int}\")]\n" +       // 10
            "        public void GivenTheSecondNumberIs(int p0) { }\n" + // 11
            "    }\n" +
            "}\n";

        var implementation = new ProjectBindingImplementation(
            "N.Steps.The_First_Number_Is_P0(Int32)", new[] { "System.Int32" },
            new SourceLocation(CsUri.GetFileSystemPath()!, sourceFileLine: 8, sourceFileColumn: 9));
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given,
            new Regex(@"^(?i)The(?:[^\w\p{Sc}]*)First(?:[^\w\p{Sc}]*)Number(?:[^\w\p{Sc}]*)Is(?:[^\w\p{Sc}]*)(?<p0>.*?)(?:[^\w\p{Sc}]*)$"),
            null, implementation, specifiedExpression: null, error: null, attributeSourceLine: 7);

        var literal = await CreateSut(csText).FindAttributeLiteralAsync(CsUri, binding);

        literal.Should().BeNull(
            "a method-name-style binding has no literal to find, and must not fall back to an unrelated method's");
    }

    // ── Shared syntax-tree cache wiring (issue #491) ────────────────────────────────────

    /// <summary>Wraps a real <see cref="CSharpSyntaxTreeCache"/>, recording the root returned on
    /// each call — used to confirm <see cref="CSharpAttributeLiteralResolver"/> routes through the
    /// shared cache instead of parsing the file itself on every call.</summary>
    private sealed class RecordingSyntaxTreeCache : ICSharpSyntaxTreeCache
    {
        private readonly CSharpSyntaxTreeCache _inner = new();
        public List<SyntaxNode> ReturnedRoots { get; } = new();

        public SyntaxNode? GetOrParseFromDisk(string filePath, IFileSystemForIDE fileSystem)
            => _inner.GetOrParseFromDisk(filePath, fileSystem);

        public SyntaxNode GetOrParse(string filePath, string text)
        {
            var root = _inner.GetOrParse(filePath, text);
            ReturnedRoots.Add(root);
            return root;
        }

        public void Invalidate(string filePath) => _inner.Invalidate(filePath);
    }

    [Fact]
    public async Task FindAttributeLiteralAsync_reuses_the_cached_parse_for_a_second_binding_in_the_same_unchanged_file()
    {
        // Regression coverage for issue #491: RenameTargetsHandler's multi-attribute picker calls
        // FindAttributeLiteralAsync once per binding attribute found on the same method — which used
        // to re-parse the whole file from scratch on every call. Two bindings against the same
        // unchanged file must share one parsed root.
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"a step\")]\n" +
            "        [When(\"a step\")]\n" +
            "        public void AStep() { }\n" +
            "    }\n" +
            "}\n";

        var documentBuffer = Substitute.For<IDocumentBufferService>();
        documentBuffer
            .TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>())
            .Returns(ci =>
            {
                ci[1] = new DocumentBuffer(CsUri, 1, csText);
                return true;
            });

        var cache = new RecordingSyntaxTreeCache();
        var sut = new CSharpAttributeLiteralResolver(
            new CSharpFileTextCache(), documentBuffer, Substitute.For<IIdeSupportLogger>(),
            new FileSystemForIDE(), cache);

        var implementation = new ProjectBindingImplementation(
            "N.Steps.AStep()", Array.Empty<string>(),
            new SourceLocation(CsUri.GetFileSystemPath()!, sourceFileLine: 9, sourceFileColumn: 9));
        var givenBinding = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given, new Regex("^a step$"), null, implementation,
            specifiedExpression: "a step", error: null, attributeSourceLine: 7);
        var whenBinding = new ProjectStepDefinitionBinding(
            ScenarioBlock.When, new Regex("^a step$"), null, implementation,
            specifiedExpression: "a step", error: null, attributeSourceLine: 8);

        await sut.FindAttributeLiteralAsync(CsUri, givenBinding);
        await sut.FindAttributeLiteralAsync(CsUri, whenBinding);

        cache.ReturnedRoots.Should().HaveCount(2);
        cache.ReturnedRoots[1].Should().BeSameAs(cache.ReturnedRoots[0]);
    }
}
