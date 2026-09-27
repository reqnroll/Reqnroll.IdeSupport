using System.IO;

namespace Reqnroll.IdeSupport.VisualStudio.Tests.WellKnownIds;

/// <summary>
/// Source-level guards against the class of bug behind issues #747 and #774: a hand-typed VS
/// identifier that compiles fine but names the wrong thing. Scans the checked-in sources of the
/// Extension and VSSDKIntegration projects, so it needs no VS install.
/// </summary>
/// <remarks>
/// When one of these fails, prefer the SDK constant (<c>VSConstants</c>, <c>VsMenus</c>,
/// <c>__VSPROPID</c>, <c>PredefinedClassificationTypeNames</c>, …). Only if none exists, add the
/// value to <c>VsWellKnownIds</c> with where it came from and a header test or a
/// <c>VsWellKnownIdsSelfCheck</c> entry.
/// </remarks>
public class MagicValueSourceGuardTests
{
    /// <summary>
    /// Files allowed to contain GUID literals: identifiers this extension owns (its package, VSIX and
    /// command-set IDs) plus the few VS identifiers with no SDK constant, each documented where it lives.
    /// </summary>
    private static readonly HashSet<string> GuidLiteralAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "VsWellKnownIds.cs",           // undocumented VS IDs, runtime-checked by VsWellKnownIdsSelfCheck
        "ShellMenuIds.cs",             // VS.Extensibility can't read VsMenus; asserted equal to it
        "TestExplorerCommandIds.cs",   // undocumented Test Explorer IDs, runtime-checked
        "HookCodeLensCommandIds.cs",   // our own command set, checked against the .vsct
        "ReqnrollPluginPackage.cs",    // our own package GUID, checked against the .vsct
    };

    private static readonly Regex GuidLiteral = new(
        @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}", RegexOptions.Compiled);

    // A numeric literal as any argument of a COM property accessor — e.g. GetProperty(0x0000000B, …)
    // (issue #774) or GetProperty(itemId, -8005, …). Property IDs must come from the SDK enums.
    // The argument list may contain one level of parentheses, e.g. a "(int)__VSHPROPID.X" cast.
    private static readonly Regex NumericPropertyId = new(
        @"\b(Get|Set)(Guid)?Property\s*\((?<args>(?:[^()]|\([^()]*\))*)\)", RegexOptions.Compiled);
    private static readonly Regex NumericArgument = new(
        @"(^|,)\s*-?(0x[0-9A-Fa-f]+|\d+)[uUlL]*\s*(,|$)", RegexOptions.Compiled);

    private static IEnumerable<(string File, int Line, string Code)> CodeLines()
    {
        foreach (var file in RepoPaths.VsExtensionSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripLineComment(lines[i]);
                if (code.Trim().Length > 0)
                    yield return (file, i + 1, code);
            }
        }
    }

    // Good enough for a guard: drops "//…" (including "///" doc comments) unless it sits inside a
    // string literal, so provenance comments may mention the values they document.
    private static string StripLineComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            else if (!inString && line[i] == '/' && line[i + 1] == '/')
                return line.Substring(0, i);
        }
        return line;
    }

    private static string Describe(IEnumerable<(string File, int Line, string Code)> hits) =>
        string.Join(Environment.NewLine, hits.Select(h => $"{RepoPaths.Relative(h.File)}:{h.Line}: {h.Code.Trim()}"));

    [Fact]
    public void Guid_literals_only_appear_in_allow_listed_files()
    {
        var hits = CodeLines()
            .Where(l => GuidLiteral.IsMatch(l.Code) && !GuidLiteralAllowList.Contains(Path.GetFileName(l.File)))
            .ToList();

        hits.Should().BeEmpty(
            "VS GUIDs should come from an SDK constant, or live in VsWellKnownIds with their provenance:{0}{1}",
            Environment.NewLine, Describe(hits));
    }

    [Fact]
    public void Com_property_ids_are_never_numeric_literals()
    {
        var hits = CodeLines()
            .Where(l => NumericPropertyId.Matches(l.Code).Cast<Match>()
                .Any(m => NumericArgument.IsMatch(m.Groups["args"].Value)))
            .ToList();

        hits.Should().BeEmpty(
            "property IDs should use the SDK enums (__VSPROPID, __VSHPROPID, …), never a hand-typed number (issue #774):{0}{1}",
            Environment.NewLine, Describe(hits));
    }

    [Fact]
    public void The_Gherkin_content_type_name_is_only_spelled_out_once()
    {
        var hits = CodeLines()
            .Where(l => l.Code.Contains("\"Gherkin\"") && Path.GetFileName(l.File) != "VsWellKnownIds.cs")
            .ToList();

        hits.Should().BeEmpty(
            "use VsWellKnownIds.GherkinContentType so the content type cannot drift from the registered document type:{0}{1}",
            Environment.NewLine, Describe(hits));
    }

    [Fact]
    public void The_CSharp_content_type_name_is_only_spelled_out_once()
    {
        var hits = CodeLines()
            .Where(l => l.Code.Contains("\"CSharp\"") && Path.GetFileName(l.File) != "CSharpDocumentType.cs")
            .ToList();

        hits.Should().BeEmpty("use CSharpDocumentType.CSharp:{0}{1}", Environment.NewLine, Describe(hits));
    }

    [Fact]
    public void Classification_base_definitions_use_predefined_names()
    {
        var hits = CodeLines().Where(l => l.Code.Contains("BaseDefinition(\"")).ToList();

        hits.Should().BeEmpty(
            "use PredefinedClassificationTypeNames (or one of our own classification-name constants) — a misspelled base silently loses its default colours:{0}{1}",
            Environment.NewLine, Describe(hits));
    }

    [Theory]
    [InlineData("solution.GetProperty(0x0000000B, out var isOpen);")]
    [InlineData("hier.GetProperty(itemId, -8005, out var value);")]
    [InlineData("hier.GetGuidProperty(4294967294u, (int)__VSHPROPID.VSHPROPID_TypeGuid, out var guid);")]
    public void The_property_id_guard_catches_numeric_literals(string code)
    {
        NumericPropertyId.Matches(code).Cast<Match>()
            .Any(m => NumericArgument.IsMatch(m.Groups["args"].Value))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("solution.GetProperty((int)__VSPROPID.VSPROPID_IsSolutionOpen, out var isOpen);")]
    [InlineData("hier.GetGuidProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectIDGuid, out var guid);")]
    [InlineData("var p = type.GetProperty(\"Result\");")]
    public void The_property_id_guard_allows_sdk_enums_and_reflection(string code)
    {
        NumericPropertyId.Matches(code).Cast<Match>()
            .Any(m => NumericArgument.IsMatch(m.Groups["args"].Value))
            .Should().BeFalse();
    }
}
