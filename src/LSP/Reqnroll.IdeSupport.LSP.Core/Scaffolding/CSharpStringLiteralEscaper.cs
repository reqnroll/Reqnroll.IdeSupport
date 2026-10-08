#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.Scaffolding;

/// <summary>
/// Escapes text so it can be embedded safely inside a generated C# string literal.
/// </summary>
/// <remarks>
/// Local to the scaffolding feature. The rename side already has a similar helper
/// (<c>CSharpAttributeLiteralResolver.FormatLiteral</c>, #935); consolidating the two
/// into a single shared helper is a tracked follow-up, deliberately not done here so
/// this fix does not depend on the still-unmerged #935 work.
/// </remarks>
internal static class CSharpStringLiteralEscaper
{
    /// <summary>
    /// Escapes for a regular C# string literal (<c>"..."</c>): backslash first, then
    /// the double quote, so an embedded <c>\</c> or <c>"</c> cannot terminate the
    /// literal or form an invalid escape sequence.
    /// </summary>
    internal static string EscapeRegular(string text) => text
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");

    /// <summary>
    /// Escapes for a verbatim C# string literal (<c>@"..."</c>): only embedded double
    /// quotes are doubled. Backslashes must NOT be re-escaped — in a verbatim literal
    /// they are already literal and doubling them corrupts regex expressions.
    /// </summary>
    internal static string EscapeVerbatim(string text) => text
        .Replace("\"", "\"\"");
}
