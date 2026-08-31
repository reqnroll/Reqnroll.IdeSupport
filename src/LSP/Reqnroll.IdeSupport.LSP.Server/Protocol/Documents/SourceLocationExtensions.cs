#nullable enable

using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol.Documents;

/// <summary>
/// Converts <see cref="SourceLocation"/> values (1-based, discovery layer) to OmniSharp LSP types.
/// </summary>
public static class SourceLocationExtensions
{
    /// <summary>
    /// Converts a <see cref="SourceLocation"/> to an LSP <see cref="Location"/> for use in
    /// <c>textDocument/definition</c> responses.  Always produces a zero-width range at the
    /// start position — consistent with LSP convention where the definition range identifies
    /// the symbol, not its body.  End-position data from the discovery layer (e.g. PDB
    /// sequence-point spans from the Connector) is intentionally discarded here.
    /// </summary>
    public static Location ToLspLocation(this SourceLocation loc)
    {
        // SourceLocation is 1-based; LSP positions are 0-based.
        var line = loc.SourceFileLine - 1;
        var ch   = loc.SourceFileColumn - 1;

        return new Location
        {
            Uri   = DocumentUri.FromFileSystemPath(loc.SourceFile),
            Range = new LspRange(
                new Position(line, ch),
                new Position(line, ch))
        };
    }

    /// <summary>
    /// Returns a <see cref="SourceLocation"/> refined to the method identifier token rather
    /// than the method body start.
    /// <para>
    /// The connector discovery path stores the first PDB sequence point — typically the
    /// opening <c>{</c> of the method body.  This helper reads a small backward window in the
    /// source file to locate the line that contains the method name, which is what the Roslyn
    /// discovery path reports and what produces a useful "Code" column in the VS declarations
    /// window.  When the file is inaccessible or the name cannot be found the original location
    /// is returned unchanged.
    /// </para>
    /// </summary>
    /// <param name="loc">The location to refine (typically pointing at <c>{</c>).</param>
    /// <param name="qualifiedMethod">
    /// The qualified method name from the binding, e.g. <c>"CalculatorSteps.AddNumbers(int, int)"</c>.
    /// Only the simple name after the last <c>.</c> (and before any <c>(</c>) is used for the search.
    /// </param>
    public static SourceLocation WithIdentifierLocation(this SourceLocation loc, string? qualifiedMethod,
        IFileSystemForIDE fileSystem)
    {
        var simpleName = ExtractSimpleMethodName(qualifiedMethod);
        if (simpleName is null || string.IsNullOrEmpty(loc.SourceFile))
            return loc;

        try
        {
            var lines    = fileSystem.File.ReadAllLines(loc.SourceFile);
            // Convert 1-based line to 0-based array index; clamp to file bounds.
            var startIdx = Math.Min(loc.SourceFileLine - 1, lines.Length - 1);
            var endIdx   = Math.Max(0, startIdx - 6);

            for (var i = startIdx; i >= endIdx; i--)
            {
                var col = lines[i].IndexOf(simpleName, StringComparison.Ordinal);
                if (col >= 0)
                    return new SourceLocation(loc.SourceFile, i + 1, col + 1, i + 1, col + simpleName.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            // Source file not accessible, or not a path this platform can even open — return the
            // original location. IOException alone was too narrow: loc.SourceFile is a path this
            // method never validated (it can come straight from a PDB written on another platform),
            // and the argument/permission failures that shape can produce were escaping into the
            // definition handler and faulting the whole request (issue #540 F8).
        }

        return loc;
    }

    internal static string? ExtractSimpleMethodName(string? qualifiedMethod)
    {
        if (string.IsNullOrEmpty(qualifiedMethod)) return null;
        var s = qualifiedMethod.AsSpan();
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        var dot = s.LastIndexOf('.');
        return (dot >= 0 ? s[(dot + 1)..] : s).ToString();
    }
}
