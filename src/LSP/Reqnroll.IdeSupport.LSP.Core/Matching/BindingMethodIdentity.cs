using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.Matching;

/// <summary>
/// Normalizes a binding's method name and parameter types to a spelling-independent canonical
/// form, so the very same C# method is recognized as identical regardless of which discovery
/// path (the reflection connector or Roslyn source parsing) produced it.
/// </summary>
/// <remarks>
/// The reflection connector and Roslyn format a binding's method reference differently for the
/// very same method: the connector emits <c>"DeclaringType.MethodName(ParamType, ...)"</c> (built
/// from <c>MethodInfo</c> reflection data, no namespace -- see
/// <c>DiscoveryResultTransformer.GetMethodReference</c>), while Roslyn emits
/// <c>"Namespace.DeclaringType.MethodName"</c> (walking up syntax-tree ancestors -- see
/// <c>StepDefinitionFileParser.FullMethodName</c>), with no parameter list. Parameter types have
/// the same split: the connector reports a parameter's fully-qualified CLR type name (e.g.
/// <c>"System.Int32"</c>, effectively <c>Type.FullName</c>), while Roslyn reports the literal
/// source-code type text (e.g. <c>"int"</c> -- <c>ParameterSyntax.Type.ToString()</c>), which for
/// a C# primitive keyword doesn't even share a spelling with its CLR name.
/// <para>
/// A binding whose source file is picked up by two different discovery runs -- e.g. a project
/// that references another Reqnroll-bearing project transitively discovers that project's own
/// bindings via the connector, while the referenced project's own registry gets its matching
/// entry reconciled from source via Roslyn (an open buffer, or a file edited since the last
/// build) -- ends up with the same physical step definition represented by two literally
/// different <c>Method</c>/<c>ParameterTypes</c> strings. Without normalizing through this class,
/// every identity computed from those raw strings (<see cref="BindingId"/>, and
/// <c>ProjectBindingRegistry.ReplaceBindings</c>'s own supersede check) treats them as two
/// unrelated bindings -- producing duplicate/misattributed rows in Find Unused Step Definitions
/// and false "0 usages" CodeLens counts for a step that is, in fact, used (issue #547/#548).
/// </para>
/// </remarks>
internal static class BindingMethodIdentity
{
    private static readonly Dictionary<string, string> CSharpKeywordToClrTypeName = new(StringComparer.Ordinal)
    {
        ["bool"] = "Boolean", ["byte"] = "Byte", ["sbyte"] = "SByte", ["char"] = "Char",
        ["decimal"] = "Decimal", ["double"] = "Double", ["float"] = "Single",
        ["int"] = "Int32", ["uint"] = "UInt32", ["long"] = "Int64", ["ulong"] = "UInt64",
        ["short"] = "Int16", ["ushort"] = "UInt16", ["string"] = "String", ["object"] = "Object",
    };

    // Matches each dotted identifier chain within a parameter-type string (the type itself, and
    // every generic type argument), so a generic/array/nullable type's namespace-qualified pieces
    // -- e.g. the "System.Collections.Generic"/"String"/"Int32" inside
    // "System.Collections.Generic.Dictionary<String,Int32>" -- are each reduced independently,
    // rather than only the string's own trailing segment (which a plain Split('.').Last() would
    // do, incorrectly leaving everything up to the first type argument's namespace untouched).
    private static readonly Regex DottedIdentifierRegex =
        new(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Reduces a binding's <c>ProjectBindingImplementation.Method</c> to its trailing
    /// "DeclaringType.MethodName" segment, discarding any parameter-signature suffix and
    /// namespace prefix -- the part that is spelled identically regardless of discovery path.
    /// </summary>
    public static string NormalizeMethod(string? method)
    {
        if (string.IsNullOrEmpty(method))
            return string.Empty;

        var withoutSignature = method!.Split('(')[0];
        var segments = withoutSignature.Split('.');
        return segments.Length <= 2
            ? withoutSignature
            : string.Join(".", segments.Skip(segments.Length - 2));
    }

    /// <summary>
    /// Reduces a parameter type to a spelling-independent canonical form: strips whitespace, then
    /// reduces every dotted identifier chain in the string -- the type itself *and* each of its
    /// generic type arguments -- to its trailing segment, mapping a C# primitive keyword to its
    /// CLR simple name. Array (<c>[]</c>), generic (<c>&lt;,&gt;</c>), and nullable (<c>?</c>)
    /// punctuation is left as-is, which is already spelled identically on both sides.
    /// </summary>
    /// <remarks>
    /// Confirmed live against the Quickstart sample (issue #515 follow-up): fixing only the
    /// top-level type name (treating the whole string as one identifier) corrected parameterless
    /// and simple-typed methods, but left every generic/array/nullable-typed method duplicated --
    /// e.g. the connector's "System.Collections.Generic.Dictionary&lt;String,Int32&gt;" never
    /// matched Roslyn's "Dictionary&lt;string, int&gt;" even after that narrower fix, since neither
    /// the inner type arguments' spelling nor the space after the comma were normalized.
    /// </remarks>
    public static string NormalizeParameterType(string type)
    {
        var withoutWhitespace = WhitespaceRegex.Replace(type, string.Empty);
        return DottedIdentifierRegex.Replace(withoutWhitespace, match =>
        {
            var simpleName = match.Value.Split('.').Last();
            return CSharpKeywordToClrTypeName.TryGetValue(simpleName, out var clrName) ? clrName : simpleName;
        });
    }
}
