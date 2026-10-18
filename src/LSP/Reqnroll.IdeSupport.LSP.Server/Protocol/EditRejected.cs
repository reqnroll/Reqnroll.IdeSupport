using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// The single server-side way to reject an edit-producing request (rename, formatting, …) with a
/// reason the user can act on (issue #1132).
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract.</b> A rejection is a JSON-RPC error response with code
/// <see cref="Code"/> (<c>-32803</c>, LSP <c>RequestFailed</c>: "the request was syntactically
/// valid but could not complete"), a <c>message</c> that is short, actionable and safe to show to the
/// user <i>verbatim</i>, and an optional <c>data</c> string naming what was rejected (for example a
/// rename validation scope).
/// </para>
/// <para>
/// Clients show the message of a <see cref="Code"/> error to the user (modal dialog) and treat every
/// other error code as an unexpected failure: log it and at most show a generic message.
/// <c>ContentModified</c> (<c>-32801</c>) is a routine, retryable cancellation and is never a
/// rejection.
/// </para>
/// <para>
/// A request that has nothing to change ("already formatted") is <b>not</b> a rejection: return an
/// empty edit result. Handlers must not throw this for conditions the user cannot correct, and must
/// not use it for on-type/automatic requests that fire while the user is typing.
/// </para>
/// </remarks>
public static class EditRejected
{
    /// <summary>The LSP <c>RequestFailed</c> error code that marks a user-correctable rejection.</summary>
    public const int Code = ErrorCodes.RequestFailed;

    /// <summary>
    /// Builds the <see cref="RpcErrorException"/> for a rejected edit request.
    /// </summary>
    /// <param name="message">The user-facing reason; shown verbatim by the clients.</param>
    /// <param name="scope">Optional machine-readable name of what was rejected, sent as the error's <c>data</c>.</param>
    public static RpcErrorException Error(string message, string? scope = null) =>
        // RpcErrorException's `error` parameter isn't nullable-annotated (it predates this
        // codebase's NRT adoption), but a null `data` is handled fine at the wire level —
        // ErrorMessage.Data is [JsonProperty(NullValueHandling = NullValueHandling.Ignore)].
        new(Code, scope!, message);
}
