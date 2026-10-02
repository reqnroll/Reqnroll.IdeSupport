#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio;

/// <summary>
/// Static bridge that the Extension project populates so the VSSDK command filter
/// (which has no reference to <c>LspInterceptingPipe</c>) can invoke the comment toggle.
/// </summary>
/// <remarks>
/// Set by <c>ReqnrollLanguageClient</c> once the server connection is established;
/// cleared on dispose. Safe to call from any thread.
/// </remarks>
public static class CommentToggleRedirect
{
    /// <summary>
    /// Delegate set by the Extension project: <c>(fileUri, startLine, endLine, mode, ct) → Task</c>.
    /// Null when the server has not yet initialized or has been disposed.
    /// </summary>
    public static Func<string, int, int, CommentToggleMode, CancellationToken, Task>? ToggleCommentAsync { get; set; }
}
