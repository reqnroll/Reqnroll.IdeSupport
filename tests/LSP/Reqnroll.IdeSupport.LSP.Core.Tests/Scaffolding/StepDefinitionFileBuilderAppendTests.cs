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
    public void Does_not_double_indent_the_appended_method_file_scoped()
    {
        // Regression test: the target file's detected member indent (4 spaces here, matching the
        // snippet's own baked-in indent unit) must not be added *on top of* that baked-in indent.
        var existing =
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
        result.Should().Contain("\r\n    [When(@\"I press add\")]\r\n");
        result.Should().Contain("\r\n    public void WhenIPressAdd()\r\n");
        result.Should().Contain("\r\n    {\r\n        throw new PendingStepException();\r\n    }\r\n");
        result.Should().NotContain("        [When(@\"I press add\")]"); // would indicate doubled (8-space) indent
    }

    [Fact]
    public void Matches_existing_two_level_indent_when_appending_to_a_block_scoped_namespace()
    {
        // Block-scoped files conventionally indent members two levels (namespace + class), unlike
        // file-scoped ones (one level). The appended method must match that, not the snippet's own
        // single baked-in level.
        var existing =
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
        result.Should().Contain("\r\n        [When(@\"I press add\")]\r\n");
        result.Should().Contain("\r\n        public void WhenIPressAdd()\r\n");
        result.Should().Contain("\r\n        {\r\n            throw new PendingStepException();\r\n        }\r\n");
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
    // ── Roslyn-based class-body location (issue #586) ────────────────────────────
    // The first case below is the one the previous hand-rolled scan genuinely got wrong.
    // The rest are characterization tests: the old scan handled them correctly too (its
    // quote-pairing happened to mask the offending braces), and they are pinned here so a
    // future change to the locator cannot quietly regress them.

    [Fact]
    public void Appends_when_a_raw_string_literal_contains_a_quote_run()
    {
        // The previously-failing case. A four-quote raw string whose body contains a literal
        // `"""` defeats naive quote pairing: the old scan mis-tracked where the literal ended,
        // saw the unbalanced `{` inside it as real code, failed to balance the class braces, and
        // returned null — silently downgrading "Define missing step" to "create new file".
        // Built by concatenation rather than a raw string, since the fixture itself contains
        // raw-string delimiters.
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    private const string Json = \"\"\"\"\r\n" +
            "        say \"\"\" and an unbalanced {\r\n" +
            "        \"\"\"\";\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
        result.Should().Contain("unbalanced");
    }

    [Fact]
    public void Appends_when_an_interpolated_string_contains_braces()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    [Given(@\"a step\")]\r\n" +
            "    public void GivenAStep()\r\n" +
            "    {\r\n" +
            "        var s = $\"x {(true ? \"}\" : \"y\")} z\";\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
        result.Should().Contain("GivenAStep");
    }

    [Fact]
    public void Appends_when_a_verbatim_interpolated_string_contains_a_brace()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    public void M()\r\n" +
            "    {\r\n" +
            "        var s = $@\"a {1} b } c\";\r\n" +
            "    }\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
    }

    [Fact]
    public void Appends_when_a_char_literal_holds_a_brace()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    private char _c = '}';\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
    }

    [Fact]
    public void Appends_to_the_first_class_when_the_file_declares_several()
    {
        // Matches the previous scan's "first `class` keyword wins" behaviour.
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "public class FirstStepDefinitions\r\n" +
            "{\r\n" +
            "}\r\n" +
            "\r\n" +
            "public class SecondStepDefinitions\r\n" +
            "{\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        var firstIndex  = result!.IndexOf("FirstStepDefinitions", StringComparison.Ordinal);
        var appendIndex = result.IndexOf("WhenIPressAdd", StringComparison.Ordinal);
        var secondIndex = result.IndexOf("SecondStepDefinitions", StringComparison.Ordinal);

        appendIndex.Should().BeGreaterThan(firstIndex);
        appendIndex.Should().BeLessThan(secondIndex);
    }

    [Fact]
    public void Returns_null_when_a_block_comment_is_unterminated()
    {
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    /* never closed\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().BeNull();
    }

    [Fact]
    public void Appends_despite_an_ordinary_syntax_error_elsewhere_in_the_file()
    {
        // Only unterminated lexical structure blocks the append. A missing semicolon leaves the
        // class braces perfectly locatable, and the previous scan tolerated it too — so a file
        // that is merely mid-edit must not be downgraded to the create-new-file fallback.
        var existing =
            "namespace MyNamespace.MyProject;\r\n" +
            "\r\n" +
            "[Binding]\r\n" +
            "public class CalculatorStepDefinitions\r\n" +
            "{\r\n" +
            "    private int _x = 1\r\n" +
            "}\r\n";

        var result = StepDefinitionFileBuilder.AppendToFile(existing, new[] { Snippet }, "    ", "\r\n");

        result.Should().NotBeNull();
        result.Should().Contain("WhenIPressAdd");
    }
}
