using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Bindings;

/// <summary>
/// The source backfills (<see cref="BindingImporter.TryGetMethodIdentifierLocation"/> and
/// <see cref="BindingImporter.TryGetAttributeSourceLine(SyntaxNode,string,ScenarioBlock,string,MethodIdentity)"/>)
/// select the declaration a binding's <see cref="MethodIdentity"/> describes, rather than the first
/// one with its name.
/// </summary>
public class MethodIdentityMatcherTests
{
    private const string Source = """
        using System.Collections.Generic;

        namespace First
        {
            public class Steps
            {
                [Given("no arguments")]
                public void Bind() { }

                [Given("a number {int}")]
                public void Bind(int value) { }

                [Given("a text {string}")]
                public void Bind(string value) { }

                [Given("some books")]
                public void Bind(List<Book> books) { }

                [Given("maybe a number")]
                public void Bind(int? value, Book[,] grid) { }
            }

            public class Book { }
        }

        namespace Second
        {
            public class Steps
            {
                [Given("the second class")]
                public void Bind(int value) { }
            }
        }
        """;

    private static readonly SyntaxNode Root = CSharpSyntaxTree.ParseText(Source).GetRoot();

    private static MethodIdentity Identity(string documentationCommentId) =>
        new("Bindings, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", documentationCommentId);

    private static int LineOf(string text) =>
        Source.Replace("\r\n", "\n").Split('\n').ToList().FindIndex(l => l.Contains(text)) + 1;

    [Theory]
    [InlineData("M:First.Steps.Bind", "public void Bind() { }")]
    [InlineData("M:First.Steps.Bind(System.Int32)", "public void Bind(int value) { }")]
    [InlineData("M:First.Steps.Bind(System.String)", "public void Bind(string value) { }")]
    [InlineData("M:First.Steps.Bind(System.Collections.Generic.List{First.Book})", "public void Bind(List<Book> books) { }")]
    [InlineData("M:First.Steps.Bind(System.Nullable{System.Int32},First.Book[0:,0:])", "public void Bind(int? value, Book[,] grid) { }")]
    [InlineData("M:Second.Steps.Bind(System.Int32)", "[Given(\"the second class\")]")]
    public void Method_identifier_backfill_selects_the_identified_overload(string documentationCommentId, string expectedLineText)
    {
        var location = BindingImporter.TryGetMethodIdentifierLocation(Root, "Bind", Identity(documentationCommentId));

        var expectedLine = LineOf(expectedLineText);
        if (expectedLineText.StartsWith("["))
            expectedLine++; // the method is declared on the line after its attribute
        location.Should().NotBeNull();
        location!.Value.Line.Should().Be(expectedLine);
    }

    [Fact]
    public void Without_an_identity_the_first_method_by_name_still_wins()
    {
        BindingImporter.TryGetMethodIdentifierLocation(Root, "Bind")!.Value.Line
            .Should().Be(LineOf("public void Bind() { }"));
    }

    [Fact]
    public void An_identity_that_no_longer_matches_the_source_falls_back_to_the_name_match()
    {
        // The source changed since the build: neither the type nor a three-parameter Bind exists any more.
        BindingImporter.TryGetMethodIdentifierLocation(Root, "Bind", Identity("M:Third.Steps.Bind(System.Double,System.Double,System.Double)"))!.Value.Line
            .Should().Be(LineOf("public void Bind() { }"));
    }

    [Fact]
    public void Attribute_line_backfill_ignores_the_attributes_of_other_overloads()
    {
        // With no expression to tell them apart, the first attribute among all "Bind" methods used
        // to win; the identity restricts the search to the identified overload.
        var line = BindingImporter.TryGetAttributeSourceLine(Root, "Bind", ScenarioBlock.Given, expression: null,
            Identity("M:Second.Steps.Bind(System.Int32)"));

        line.Should().Be(LineOf("[Given(\"the second class\")]"));
    }

    [Theory]
    [InlineData("M:A.B.C", "A.B.C", new string[0])]
    [InlineData("M:A.B.C(System.Int32,System.String)", "A.B.C", new[] { "Int32", "String" })]
    [InlineData("M:A.B.C(System.Collections.Generic.Dictionary{System.String,System.Collections.Generic.List{System.Int32}})",
        "A.B.C", new[] { "Dictionary<String,List<Int32>>" })]
    [InlineData("M:A.B.C(System.Nullable{System.Guid},System.Int32[],System.Int32[0:,0:])",
        "A.B.C", new[] { "Guid?", "Int32[]", "Int32[,]" })]
    public void Documentation_comment_ids_parse_into_comparable_parts(string id, string expectedName, string[] expectedParameters)
    {
        MethodIdentityMatcher.TryParse(id, out var name, out var parameters).Should().BeTrue();

        name.Should().Be(expectedName);
        parameters.Should().Equal(expectedParameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("T:A.B")]
    [InlineData("M:A.B.C(System.Int32")]
    [InlineData("M:A.B.C(System.List{System.Int32)")]
    public void Malformed_ids_do_not_parse(string? id) =>
        MethodIdentityMatcher.TryParse(id, out _, out _).Should().BeFalse();
}
