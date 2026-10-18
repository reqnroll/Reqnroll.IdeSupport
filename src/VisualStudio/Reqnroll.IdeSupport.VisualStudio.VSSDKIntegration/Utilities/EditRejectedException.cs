#nullable enable

using System;

namespace Reqnroll.IdeSupport.VisualStudio.Utilities;

/// <summary>
/// The language server rejected an edit-producing request (rename, formatting, …) with a reason the
/// user can act on: a JSON-RPC error with code <see cref="ErrorCode"/> (LSP <c>RequestFailed</c>)
/// whose <c>message</c> is meant to be shown to the user verbatim (issue #1132).
/// </summary>
/// <remarks>
/// Any other error code is an unexpected failure and is logged, not shown; <c>ContentModified</c>
/// (<see cref="LspContentModifiedException.ErrorCode"/>) is a routine retryable cancellation and is
/// never a rejection. Lives here, not in the Extension project, so the VSSDK-side command filters can
/// catch it.
/// </remarks>
public sealed class EditRejectedException : Exception
{
    /// <summary>The LSP <c>RequestFailed</c> error code that marks a user-correctable rejection.</summary>
    public const int ErrorCode = -32803;

    /// <summary>Creates the exception carrying the server's user-facing <paramref name="message"/>.</summary>
    public EditRejectedException(string message) : base(message) { }
}
