#nullable enable

using Reqnroll.IdeSupport.LSP.Core.Scaffolding;
using Xunit;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Scaffolding;

public class StepDefinitionFileBuilderAppendTests
{
    private const string Snippet = """
            [When(@"I press add")]
            public void WhenIPressAdd()
            {
                throw new PendingStepException();
            }

        """;

    [Fact]
    public void Inserts_new_snippet_before_class_closing_brace_block_scoped()
    {
        var existing =
            "using System;\r\n" +
            "using Reqnroll;\r\n" +
            "\r\n" +
            "namespace MyNamespace.MyProject\r\n" +
            "{\r\n" +
            "    [Binding]\r\n" +
            "    public class CalculatorStepDefinitions\r\n" +
            "    {\r\n" +
            "        [Given(@\"a step\")]\r\n" +
            "        public void GivenAStep()\r\n" +
            "        {\r\n" +
            "            throw new PendingStepException();\r\n" +
            "        }\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
        result.Should().Contain("GivenAStep");

        // The new method lands after the existing one, inside the same class body.
        result!.IndexOf("WhenIPressAdd").Should().BeGreaterThan(result.IndexOf("GivenAStep"));
        result.Should().Contain("namespace MyNamespace.MyProject");
    }

    [Fact]
    public void Inserts_new_snippet_before_class_closing_brace_file_scoped()
    {
        var existing =
            "using System;\r\n" +
            "using Reqnroll;\r\n" +
            "\r\n" +
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    [Given(@\"a step\")]\r\n" +
            "    public void GivenAStep()\r\n" +
            "    {\r\n" +
            "        throw new PendingStepException();\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
        result.Should().Contain("GivenAStep");
    }

    [Fact]
    public void Appends_into_an_empty_class_body()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
    }

    [Fact]
    public void Ignores_braces_inside_string_literals_and_comments()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    // a comment with a brace }\r\n" +
            "    [Given(@\"a step with a curly brace: {oops}\")]\r\n" +
            "    public void GivenAStep()\r\n" +
            "    {\r\n" +
            "        var s = \"another } literal\";\r\n" +
            "        throw new PendingStepException();\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
        result.Should().Contain("GivenAStep");
    }

    [Fact]
    public void Returns_null_when_no_class_keyword_is_found()
    {
        var existing = "namespace MyNamespace.MyProject;\r\n// no class here\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_null_when_a_string_literal_is_unterminated()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    var s = \"unterminated\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().BeNull();
    }

    [Fact]
    public void Returns_existing_content_unchanged_when_no_snippets_are_given()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, Array.Empty<string>(), "    ", "\r\n");

        result.Should().Be(existing);
    }
}
