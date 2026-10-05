#nullable enable

using System.Threading.Tasks;

namespace Reqnroll.IdeSupport.VisualStudio.GoToDefinition;

/// <summary>
/// Static bridge that the Extension project populates so the VSSDK command filter
/// (which has no reference to <c>LspInterceptingPipe</c>) can run Go To Definition itself (issue #757).
/// </summary>
/// <remarks>
/// Set by <c>ReqnrollLanguageClient</c> once the server connection is established;
/// cleared on dispose. Safe to call from any thread.
/// </remarks>
public static class GoToDefinitionRedirect
{
    /// <summary>
    /// Delegate set by the Extension project: <c>(fileUri, line, character, caretLineText, ct)</c>.
    /// <c>line</c>/<c>character</c> are the 0-based caret position; <c>caretLineText</c> is the full
    /// text of the caret's line, used to title the results window when there are several definitions.
    /// Null when the server has not yet initialized or has been disposed.
    /// </summary>
    public static Func<string, int, int, string, CancellationToken, Task>? GoToDefinitionAsync { get; set; }

    /// <summary>
    /// Delegate set by the Extension project: <c>(fileUri, line, character, ct)</c> -> whether the step at
    /// that 0-based position has a binding Go To Definition can navigate to (issue #898). Lets
    /// <c>GoToDefinitionNavigableSymbolProvider</c> offer the Ctrl+hover underline only where a click would go
    /// somewhere. Null when the server has not yet initialized or has been disposed.
    /// </summary>
    public static Func<string, int, int, CancellationToken, Task<bool>>? HasNavigableDefinitionAsync { get; set; }
}
