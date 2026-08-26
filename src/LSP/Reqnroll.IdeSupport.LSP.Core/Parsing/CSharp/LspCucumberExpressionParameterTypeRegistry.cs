#nullable disable

using CucumberExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

/// <summary>
/// A minimal <see cref="IParameterTypeRegistry"/> for converting Cucumber Expressions to regex
/// during Roslyn/C# source-level binding discovery (see <see cref="StepDefinitionFileParser"/>).
/// </summary>
/// <remarks>
/// Reqnroll's own runtime registry (<c>Reqnroll.Bindings.CucumberExpressions.CucumberExpressionParameterTypeRegistry</c>)
/// builds its parameter-type set by reflecting over the target project's already-compiled
/// binding methods (to discover enum types used as parameters and any
/// <c>[StepArgumentTransformation]</c>s) — information that does not exist yet during
/// syntax-only, pre-build discovery. This registry instead knows only the standard built-in
/// Cucumber parameter types that appear literally in step-definition source
/// (<c>int</c>/<c>byte</c>/<c>short</c>/<c>long</c>/<c>float</c>/<c>double</c>/<c>decimal</c>/
/// <c>word</c>/<c>string</c>, using the exact same regex fragments as Reqnroll's runtime via
/// <see cref="ParameterTypeConstants"/> so the two never drift out of sync) and treats every
/// other name — a project-defined <c>[StepArgumentTransformation]</c> type, or an enum — as an
/// unknown type matched by <see cref="ParameterTypeConstants.AnonymousParameterRegex"/>
/// (<c>.*</c>), the same permissive fallback the connector-discovered path effectively gets for
/// such types too, so the binding can still match its steps even though the precise regex isn't
/// statically derivable without a semantic model.
/// </remarks>
internal sealed class LspCucumberExpressionParameterTypeRegistry : IParameterTypeRegistry
{
    private static readonly Dictionary<string, IParameterType> KnownTypes = new(StringComparer.Ordinal)
    {
        ["int"] = Numeric("int", ParameterTypeConstants.IntParameterRegex),
        ["byte"] = Numeric("byte", ParameterTypeConstants.IntParameterRegex),
        ["short"] = Numeric("short", ParameterTypeConstants.IntParameterRegex),
        ["long"] = Numeric("long", ParameterTypeConstants.IntParameterRegex),
        ["float"] = Numeric("float", ParameterTypeConstants.FloatParameterRegex),
        ["double"] = Numeric("double", ParameterTypeConstants.FloatParameterRegex),
        ["decimal"] = Numeric("decimal", ParameterTypeConstants.FloatParameterRegex),
        [ParameterTypeConstants.WordParameterName] =
            new LspParameterType(ParameterTypeConstants.WordParameterName, ParameterTypeConstants.WordParameterRegexps),
        [ParameterTypeConstants.StringParameterName] =
            new LspParameterType(ParameterTypeConstants.StringParameterName, ParameterTypeConstants.StringParameterRegexps),
        // The official, anonymous "{}" placeholder.
        [string.Empty] = new LspParameterType(string.Empty, ParameterTypeConstants.AnonymousParameterRegex),
    };

    private static IParameterType Numeric(string name, string regex) => new LspParameterType(name, regex);

    public IParameterType LookupByTypeName(string name) =>
        KnownTypes.TryGetValue(name, out var parameterType)
            ? parameterType
            // Unknown name (custom [StepArgumentTransformation] type, or an enum): match anything
            // rather than throwing UndefinedParameterTypeException, since the exact pattern isn't
            // statically derivable here.
            : new LspParameterType(name, ParameterTypeConstants.AnonymousParameterRegex);

    public IEnumerable<IParameterType> GetParameterTypes() => KnownTypes.Values;

    private sealed class LspParameterType : IParameterType
    {
        public LspParameterType(string name, params string[] regexStrings)
        {
            Name = name;
            RegexStrings = regexStrings;
        }

        public string[] RegexStrings { get; }
        public string Name { get; }
        // Unused: LSP.Core only needs a matching Regex, never converts/extracts argument values.
        public Type ParameterType => typeof(string);
        public int Weight => 0;
        public bool UseForSnippets => false;
    }
}
