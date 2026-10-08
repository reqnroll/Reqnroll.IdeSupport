#nullable enable

using System.Text;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;




namespace Reqnroll.IdeSupport.LSP.Core.Scaffolding;

/// <summary>
/// Converts a <see cref="StepSkeletonDescriptor"/> (or a raw undefined step) into a
/// rendered C# snippet string — the method body without the enclosing class.
/// </summary>
public static class StepSkeletonRenderer
{
    // ── Cucumber Expression parameter map ────────────────────────────────────────
    // Maps {type-name} → C# type name
    private static readonly Dictionary<string, string> CucumberTypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "int",        "int"     },
        { "float",      "float"   },
        { "double",     "double"  },
        { "string",     "string"  },
        { "word",       "string"  },
        { "bigdecimal", "decimal" },
        { "biginteger", "long"    },
        { "byte",       "byte"    },
        { "short",      "short"   },
        { "long",       "long"    },
    };

    // Matches (in priority order): {type} placeholder, single-quoted string, double-quoted string,
    // float literal, integer literal.  Float must precede int to avoid consuming only the
    // integer part of a decimal number.
    private static readonly Regex CucumberTokenRegex = new Regex(
        @"\{(?<type>[^}]*)\}|'(?<str>[^']*)'|""(?<dstr>[^""]*)""|(?<float>\b\d+\.\d+\b)|(?<int>\b\d+\b)",
        RegexOptions.Compiled);

    private static readonly Regex RegexGroupRegex = new Regex(
        @"\([^)]*\)", RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="StepSkeletonDescriptor"/> from a raw undefined step and config.
    /// </summary>
    public static StepSkeletonDescriptor BuildDescriptor(
        UndefinedStepDescriptor step,
        SnippetExpressionStyle  style)
    {
        bool isCucumber = style.IsCucumber();
        bool isAsync    = style.IsAsync();

        var (expression, parameters) = isCucumber
            ? BuildCucumberExpression(step.StepText)
            : BuildRegexExpression(step.StepText);

        var methodName = BuildMethodName(step.ScenarioBlock, step.StepText, isCucumber, isAsync);

        return new StepSkeletonDescriptor(
            Block:          step.ScenarioBlock,
            Style:          style,
            ExpressionText: expression,
            Parameters:     parameters,
            MethodName:     methodName,
            IsAsync:        isAsync);
    }

    /// <summary>
    /// Renders a <see cref="StepSkeletonDescriptor"/> into a C# snippet (indented method body).
    /// The caller is responsible for supplying consistent indentation.
    /// </summary>
    public static string Render(
        StepSkeletonDescriptor descriptor,
        string                 indent,
        string                 newLine)
    {
        bool useCucumber = descriptor.Style.IsCucumber();
        bool isAsync     = descriptor.IsAsync;

        var keyword   = BlockToKeyword(descriptor.Block);
        var attrValue = useCucumber
            ? $"(\"{CSharpStringLiteralEscaper.EscapeRegular(descriptor.ExpressionText)}\")"
            : $"(@\"{CSharpStringLiteralEscaper.EscapeVerbatim(descriptor.ExpressionText)}\")";

        var paramList = string.Join(", ", descriptor.Parameters.Select(p => $"{p.Type} {p.Name}"));
        var returnType = isAsync ? "async Task" : "void";
        var signature  = $"public {returnType} {descriptor.MethodName}({paramList})";

        var sb = new StringBuilder();
        sb.Append(indent).Append($"[{keyword}{attrValue}]").Append(newLine);
        sb.Append(indent).Append(signature).Append(newLine);
        sb.Append(indent).Append('{').Append(newLine);
        sb.Append(indent).Append("    throw new PendingStepException();").Append(newLine);
        sb.Append(indent).Append('}').Append(newLine);
        return sb.ToString();
    }

    // ── Expression builders ───────────────────────────────────────────────────────

    private static (string Expression, IReadOnlyList<(string Type, string Name)> Parameters)
        BuildCucumberExpression(string stepText)
    {
        var parameters = new List<(string, string)>();
        int paramIndex = 0;
        var sb  = new StringBuilder();
        int pos = 0;

        foreach (Match m in CucumberTokenRegex.Matches(stepText))
        {
            if (m.Index > pos)
                sb.Append(EscapeForCucumber(stepText.Substring(pos, m.Index - pos)));

            if (m.Groups["type"].Success)
            {
                var typeName = m.Groups["type"].Value;
                var csType   = CucumberTypeMap.TryGetValue(typeName, out var mapped) ? mapped : "object";
                parameters.Add((csType, $"p{paramIndex++}"));
                sb.Append('{').Append(typeName).Append('}');
            }
            else if (m.Groups["str"].Success || m.Groups["dstr"].Success)
            {
                parameters.Add(("string", $"p{paramIndex++}"));
                sb.Append("{string}");
            }
            else if (m.Groups["float"].Success)
            {
                parameters.Add(("float", $"p{paramIndex++}"));
                sb.Append("{float}");
            }
            else if (m.Groups["int"].Success)
            {
                parameters.Add(("int", $"p{paramIndex++}"));
                sb.Append("{int}");
            }

            pos = m.Index + m.Length;
        }

        if (pos < stepText.Length)
            sb.Append(EscapeForCucumber(stepText.Substring(pos)));

        return (sb.ToString(), parameters.AsReadOnly());
    }

    private static (string Expression, IReadOnlyList<(string Type, string Name)> Parameters)
        BuildRegexExpression(string stepText)
    {
        var escaped = EscapeForRegex(stepText);
        var parameters = new List<(string, string)>();
        int paramIndex = 0;

        // Replace (.*) capture groups with string parameters
        var result = RegexGroupRegex.Replace(escaped, _ =>
        {
            var name = $"p{paramIndex++}";
            parameters.Add(("string", name));
            return "(.*)";
        });

        return (result, parameters.AsReadOnly());
    }

    // ── Escaping ──────────────────────────────────────────────────────────────────

    internal static string EscapeForCucumber(string text)
    {
        // Order matters: backslash first to avoid double-escaping
        return text
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace("{", "\\{");
    }

    internal static string EscapeForRegex(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("{", "\\{")
            .Replace(".", "\\.")
            .Replace("|", "\\|");
    }

    // ── Method name ───────────────────────────────────────────────────────────────

    internal static string BuildMethodName(
        ScenarioBlock scenarioBlock,
        string        stepText,
        bool          isCucumber,
        bool          isAsync)
    {
        var prefix = BlockToKeyword(scenarioBlock);

        // Strip all parameter tokens (placeholders and inferred literals) before PascalCasing
        string cleaned = isCucumber
            ? CucumberTokenRegex.Replace(stepText, "")
            : RegexGroupRegex.Replace(stepText, "");

        // Split on non-alphanumeric, PascalCase each word
        var words = Regex.Split(cleaned, @"[^a-zA-Z0-9]+")
                         .Where(w => w.Length > 0)
                         .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1));

        var name = prefix + string.Concat(words);
        return isAsync ? name + "Async" : name;
    }

    private static string BlockToKeyword(ScenarioBlock block) => block switch
    {
        ScenarioBlock.Given => "Given",
        ScenarioBlock.When  => "When",
        ScenarioBlock.Then  => "Then",
        _                   => "Given",
    };
}
