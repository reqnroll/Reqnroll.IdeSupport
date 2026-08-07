using System.Text.RegularExpressions;

namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// Ports Reqnroll's own <c>string.ToIdentifier()</c> sanitization helper
/// (<c>Reqnroll.Tracing.CodeFormattingExtensions</c> in <c>Reqnroll.dll</c>) verbatim, decompiled
/// from <c>reqnroll.tools.msbuild.generation</c> 3.3.4's bundled <c>Reqnroll.dll</c>. The
/// generator calls this to turn a feature/scenario title into the C# identifier it emits
/// (<c>reqnrollFeature.Name.ToIdentifier()</c> in <c>UnitTestFeatureGenerator</c>,
/// <c>scenario.Name.ToIdentifier()</c> in <c>UnitTestMethodGenerator</c> — see
/// docs/Test-Runner-Integration-Design.md §2). The resolver needs this exact algorithm to build
/// the search key it looks up in the genuinely-parsed generated <c>.feature.cs</c> — it does not
/// predict the resolver's answer from this alone (see design doc §3).
/// </summary>
public static class ReqnrollIdentifierNaming
{
    private static readonly Regex FirstWordCharRe = new(@"(?<pre>[^\p{Ll}\p{Lu}]+)(?<fc>[\p{Ll}\p{Lu}])");
    private static readonly Regex PunctCharRe = new(@"[\n\.-]+");
    private static readonly Regex NonIdentifierRe = new(@"[^\p{Ll}\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{Nl}\p{Nd}\p{Pc}]");
    private static readonly Regex NonLatinRe = new(@"[^a-zA-Z]");
    private static readonly Regex SingleAndDoubleQuotes = new(@"['""]");

    private static readonly Dictionary<string, string> AccentReplacements = new()
    {
        { "À", "A" }, { "Á", "A" }, { "Â", "A" }, { "Ã", "A" }, { "Ä", "A" }, { "Å", "A" }, { "Æ", "AE" },
        { "Ç", "C" }, { "È", "E" }, { "É", "E" }, { "Ê", "E" }, { "Ë", "E" },
        { "Ì", "I" }, { "Í", "I" }, { "Î", "I" }, { "Ï", "I" },
        { "Ð", "D" }, { "Ñ", "N" },
        { "Ò", "O" }, { "Ó", "O" }, { "Ô", "O" }, { "Õ", "O" }, { "Ö", "O" }, { "Ø", "O" },
        { "Ù", "U" }, { "Ú", "U" }, { "Û", "U" }, { "Ü", "U" }, { "Ý", "Y" }, { "ß", "B" },
        { "à", "a" }, { "á", "a" }, { "â", "a" }, { "ã", "a" }, { "ä", "a" }, { "å", "a" }, { "æ", "ae" },
        { "ç", "c" }, { "è", "e" }, { "é", "e" }, { "ê", "e" }, { "ë", "e" },
        { "ì", "i" }, { "í", "i" }, { "î", "i" }, { "ï", "i" },
        { "ñ", "n" },
        { "ò", "o" }, { "ó", "o" }, { "ô", "o" }, { "õ", "o" }, { "ö", "o" }, { "ø", "o" },
        { "ù", "u" }, { "ú", "u" }, { "û", "u" }, { "ü", "u" }, { "ý", "y" }, { "ÿ", "y" },
        { "Ą", "A" }, { "Ł", "L" }, { "Ľ", "L" }, { "Ś", "S" }, { "Š", "S" }, { "Ş", "S" },
        { "Ť", "T" }, { "Ź", "Z" }, { "Ž", "Z" }, { "Ż", "Z" },
        { "ą", "a" }, { "ł", "l" }, { "ľ", "l" }, { "ś", "s" }, { "š", "s" }, { "ş", "s" },
        { "ť", "t" }, { "ź", "z" }, { "ž", "z" }, { "ż", "z" },
        { "Ŕ", "R" }, { "Ă", "A" }, { "Ĺ", "L" }, { "Ć", "C" }, { "Č", "C" }, { "Ę", "E" }, { "Ě", "E" },
        { "Ď", "D" }, { "Đ", "D" }, { "Ń", "N" }, { "Ň", "N" }, { "Ő", "O" }, { "Ř", "R" }, { "Ů", "U" },
        { "Ű", "U" }, { "Ţ", "T" },
        { "ŕ", "r" }, { "ă", "a" }, { "ĺ", "l" }, { "ć", "c" }, { "č", "c" }, { "ę", "e" }, { "ě", "e" },
        { "ď", "d" }, { "đ", "d" }, { "ń", "n" }, { "ň", "n" }, { "ő", "o" }, { "ř", "r" }, { "ů", "u" },
        { "ű", "u" }, { "ţ", "t" },
    };

    /// <summary>Ports <c>CodeFormattingExtensions.ToIdentifier(string)</c> verbatim.</summary>
    public static string ToIdentifier(string text)
    {
        var result = ToIdentifierPart(text);
        if (result.Length > 0 && char.IsDigit(result[0]))
            result = "_" + result;
        return result;
    }

    /// <summary>Ports <c>CodeFormattingExtensions.ToIdentifierPart(string)</c> verbatim.</summary>
    public static string ToIdentifierPart(string text)
    {
        text = RemoveQuotationCharacters(text);
        text = FirstWordCharRe.Replace(text, m => m.Groups["pre"].Value + m.Groups["fc"].Value.ToUpper());
        text = PunctCharRe.Replace(text, "_");
        text = RemoveAccentAndPunctuationChars(text);
        if (text.Length > 0)
            text = text.Substring(0, 1).ToUpper() + text.Substring(1);
        return text;
    }

    /// <summary>Ports <c>CodeFormattingExtensions.RemoveAccentAndPunctuationChars(string)</c> verbatim.</summary>
    public static string RemoveAccentAndPunctuationChars(string text)
    {
        var stripped = NonIdentifierRe.Replace(text, string.Empty);
        return NonLatinRe.Replace(stripped, m => AccentReplacements.TryGetValue(m.Value, out var replacement) ? replacement : m.Value);
    }

    /// <summary>Ports <c>CodeFormattingExtensions.RemoveQuotationCharacters(string)</c> verbatim.</summary>
    public static string RemoveQuotationCharacters(string text) => SingleAndDoubleQuotes.Replace(text, string.Empty);
}
