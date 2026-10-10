#nullable enable

using System.Text;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.CSharp;

/// <summary>
/// Renders text as a C# string literal. Shared by the rename edit (re-emitting a step-definition
/// attribute literal, #935) and step scaffolding (emitting a new attribute literal, #944).
/// </summary>
public static class CSharpStringLiteral
{
    /// <summary>
    /// Renders <paramref name="text"/> as a complete C# string literal, including the surrounding
    /// quotes and, for a verbatim literal, the <c>@</c> prefix: verbatim (<c>@"..."</c>, where
    /// embedded quotes are doubled and nothing else is escaped - backslashes must NOT be doubled,
    /// they are already literal) or regular (<c>"..."</c>, where backslashes, quotes and the common
    /// control characters are escaped).
    /// </summary>
    public static string Format(string text, bool isVerbatim)
    {
        if (isVerbatim)
            return "@\"" + text.Replace("\"", "\"\"") + "\"";

        return "\"" + EscapeRegular(text) + "\"";
    }

    private static string EscapeRegular(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:   sb.Append(c);      break;
            }
        }

        return sb.ToString();
    }
}
