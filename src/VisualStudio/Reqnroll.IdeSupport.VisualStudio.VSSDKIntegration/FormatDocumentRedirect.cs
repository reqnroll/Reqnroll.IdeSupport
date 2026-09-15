#nullable enable

using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Static bridge that the Extension project populates so the VSSDK command filter
/// (which has no reference to <c>LspInterceptingPipe</c>) can invoke Format Document/Format Selection.
/// </summary>
/// <remarks>
/// Set by <c>ReqnrollLanguageClient</c> once the server connection is established;
/// cleared on dispose. Safe to call from any thread.
/// </remarks>
public static class FormatDocumentRedirect
{
    /// <summary>
    /// Delegate set by the Extension project: <c>(fileUri, isSelection, startLine, endLine, ct) → edits</c>.
    /// <c>startLine</c>/<c>endLine</c> (0-based) are only meaningful when <c>isSelection</c> is true.
    /// Null when the server has not yet initialized or has been disposed.
    /// </summary>
    public static Func<string, bool, int, int, CancellationToken, Task<IReadOnlyList<GherkinLineRangeEdit>?>>? FormatDocumentAsync { get; set; }
}
