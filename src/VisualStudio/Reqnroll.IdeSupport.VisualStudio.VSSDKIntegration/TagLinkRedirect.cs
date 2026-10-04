#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>One clickable tag: a 0-based, end-exclusive range in a <c>.feature</c> file and the URL it opens.</summary>
public readonly record struct TagLinkEntry(int StartLine, int StartChar, int EndLine, int EndChar, string Target)
{
    /// <summary>True when the 0-based position lies within the link's range (start inclusive, end exclusive).</summary>
    public bool Contains(int line, int character) =>
        (line > StartLine || (line == StartLine && character >= StartChar)) &&
        (line < EndLine   || (line == EndLine   && character <  EndChar));
}

/// <summary>
/// Static bridge that the Extension project populates so the VSSDK navigable-symbol provider (which has
/// no reference to <c>LspInterceptingPipe</c>) can offer Ctrl+Click on clickable tags (issue #755).
/// VS's own LSP client never sends <c>textDocument/documentLink</c>, so the extension requests it itself.
/// </summary>
/// <remarks>Set by <c>ReqnrollLanguageClient</c> once the server connection is established; cleared on dispose.</remarks>
public static class TagLinkRedirect
{
    /// <summary>Delegate set by the Extension project: <c>(fileUri, ct) =&gt;</c> the links in that file. Null when the server is not initialized.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<TagLinkEntry>>>? GetLinksAsync { get; set; }

    /// <summary>
    /// Only web links are opened: the target comes from user configuration (<c>reqnroll.json</c>), which a
    /// cloned repository controls, so <c>file:</c>, <c>ms-*:</c> and other handler schemes are refused.
    /// </summary>
    public static bool IsOpenableUrl(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
