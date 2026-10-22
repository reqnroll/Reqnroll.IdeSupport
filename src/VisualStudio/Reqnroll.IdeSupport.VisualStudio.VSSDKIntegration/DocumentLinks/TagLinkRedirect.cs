#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Reqnroll.IdeSupport.VisualStudio.LineCodeLens;

namespace Reqnroll.IdeSupport.VisualStudio.DocumentLinks;

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
/// no reference to <c>LspInterceptingPipe</c>) can offer Ctrl+Click on clickable tags.
/// VS's own LSP client never sends <c>textDocument/documentLink</c>, so the extension requests it itself.
/// </summary>
/// <remarks>Set by <c>ReqnrollLanguageClient</c> once the server connection is established; cleared on dispose.</remarks>
public static class TagLinkRedirect
{
    /// <summary>Delegate set by the Extension project: <c>(fileUri, ct) =&gt;</c> the links in that file. Null when the server is not initialized.</summary>
    public static Func<string, CancellationToken, Task<IReadOnlyList<TagLinkEntry>>>? GetLinksAsync
    {
        get => _getLinksAsync;
        set
        {
            _getLinksAsync = value;
            // Buffers restored with the solution are classified before the server connects and found no link source;
            // hand them one as soon as it exists.
            if (value is not null)
                TrackerRegistry.InvalidateAll();
        }
    }

    private static volatile Func<string, CancellationToken, Task<IReadOnlyList<TagLinkEntry>>>? _getLinksAsync;

    /// <summary>
    /// Callback set by the Extension project, invoked once per Ctrl+Click that opens a tag link, so the
    /// "Command.TagLink" telemetry event is emitted by the project that owns the transmitter.
    /// </summary>
    public static Action? LinkOpened { get; set; }

    /// <summary>The trackers of every open <c>.feature</c> buffer, so a server-side change can make them re-request their links.</summary>
    internal static readonly WeakTaggerRegistry<TagLinkTracker> TrackerRegistry = new(tracker => tracker.RequestRefresh());

    /// <summary>
    /// Asks every open <c>.feature</c> buffer to re-request its links, e.g. once the project's tag patterns arrive with
    /// the server's CodeLens refresh. Safe to call from any thread.
    /// </summary>
    public static void InvalidateAll() => TrackerRegistry.InvalidateAll();

    /// <summary>
    /// Schemes that execute script or embed content rather than navigate to a tracker: the target comes from user
    /// configuration (<c>reqnroll.json</c>), which a cloned repository controls, so these are refused.
    /// </summary>
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "command"
    };

    /// <summary>
    /// Any well-formed absolute URI is opened (<c>https:</c>, <c>file:</c>, <c>jira:</c>, <c>vscode:</c>, <c>slack:</c>
    /// ...) except the schemes in <see cref="BlockedSchemes"/>.
    /// </summary>
    public static bool IsOpenableUrl(string? target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && !BlockedSchemes.Contains(uri.Scheme);
}
