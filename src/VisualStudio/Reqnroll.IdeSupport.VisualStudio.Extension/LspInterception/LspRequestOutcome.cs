#nullable enable

using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>How an owned request (see <see cref="LspInterceptingPipe.SendRequestAsync"/>) ended.</summary>
internal enum LspRequestStatus
{
    /// <summary>The server answered with a <c>result</c> — which may legitimately be JSON <c>null</c>, an empty array or an empty object.</summary>
    Succeeded,

    /// <summary>The server answered with a JSON-RPC <c>error</c> (including <c>ContentModified</c>).</summary>
    ServerError,

    /// <summary>No response arrived within the pipe's owned-request timeout.</summary>
    TimedOut,

    /// <summary>The caller's token was cancelled before a response arrived.</summary>
    Cancelled,

    /// <summary>There is no server to answer: the pipe is disposed, or the server terminated (issue #555).</summary>
    Unavailable,
}

/// <summary>
/// The full outcome of one owned request: the status says <em>whether</em> it was answered, so a
/// caller no longer has to infer failure from a <see langword="null"/> result (issues #1017, #1019, #1020).
/// </summary>
/// <remarks>
/// The older <c>SendRequestToServerAsync</c> collapsed a server error, the 60 s timeout, a terminated
/// server, a disposed pipe and cancellation into a <see langword="null"/> result — indistinguishable
/// from a server that answered JSON <c>null</c> — and each caller then mapped that to a valid-looking
/// "nothing found" answer. Here <see cref="Result"/> is non-<see langword="null"/> exactly when
/// <see cref="Status"/> is <see cref="LspRequestStatus.Succeeded"/>; a JSON <c>null</c> answer is a
/// <see cref="JTokenType.Null"/> token, so "the server said nothing" and "the request failed" are
/// different states.
/// </remarks>
internal sealed class LspRequestOutcome
{
    private LspRequestOutcome(LspRequestStatus status, JToken? result, JObject? error)
    {
        Status = status;
        Result = result;
        Error = error;
    }

    /// <summary>How the request ended.</summary>
    public LspRequestStatus Status { get; }

    /// <summary>The response's <c>result</c>; non-null iff <see cref="Status"/> is <see cref="LspRequestStatus.Succeeded"/>. May be a JSON <c>null</c> token.</summary>
    public JToken? Result { get; }

    /// <summary>The response's <c>error</c> object; non-null iff <see cref="Status"/> is <see cref="LspRequestStatus.ServerError"/>.</summary>
    public JObject? Error { get; }

    /// <summary>True when the server answered with a <c>result</c>.</summary>
    public bool Succeeded => Status == LspRequestStatus.Succeeded;

    /// <summary>True when the server rejected the request with <c>ContentModified</c>.</summary>
    public bool IsContentModified =>
        Status == LspRequestStatus.ServerError && ErrorCodeOf(Error) == LspContentModifiedException.ErrorCode;

    /// <summary>The <c>result</c> when the request succeeded and it is not JSON <c>null</c>; otherwise <see langword="null"/>.</summary>
    public JToken? ResultOrNull => Succeeded && Result!.Type != JTokenType.Null ? Result : null;

    internal static LspRequestOutcome Success(JToken result) => new(LspRequestStatus.Succeeded, result, null);
    internal static LspRequestOutcome ServerError(JObject error) => new(LspRequestStatus.ServerError, null, error);
    internal static LspRequestOutcome TimedOut() => new(LspRequestStatus.TimedOut, null, null);
    internal static LspRequestOutcome Cancelled() => new(LspRequestStatus.Cancelled, null, null);
    internal static LspRequestOutcome Unavailable() => new(LspRequestStatus.Unavailable, null, null);

    /// <summary>
    /// Returns the <c>result</c> (possibly a JSON <c>null</c> token) or throws if the request failed.
    /// For callers whose result is cached or reused, where a failure must fault rather than read as "empty".
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's own token was cancelled.</exception>
    /// <exception cref="LspContentModifiedException">The server answered <c>ContentModified</c>.</exception>
    /// <exception cref="LspRequestFailedException">Any other failure.</exception>
    public JToken GetResultOrThrow(string method, string documentUri, CancellationToken cancellationToken = default)
    {
        if (Succeeded)
            return Result!;

        if (Status == LspRequestStatus.Cancelled)
            throw new OperationCanceledException(cancellationToken);

        throw CreateException(method, documentUri);
    }

    /// <summary>The exception that describes this failed outcome; <see cref="LspContentModifiedException"/> for ContentModified.</summary>
    public Exception CreateException(string method, string documentUri)
    {
        if (IsContentModified)
            return new LspContentModifiedException(method, documentUri);
        return new LspRequestFailedException(method, documentUri, Status, Error);
    }

    /// <summary>A short, user-presentable reason for a failed outcome, e.g. for a status-bar message.</summary>
    public string DescribeFailure() => Status switch
    {
        LspRequestStatus.ServerError => Error?["message"]?.ToString() is { Length: > 0 } message
            ? $"the server reported an error: {message}"
            : "the server reported an error",
        LspRequestStatus.TimedOut    => "the server did not respond in time",
        LspRequestStatus.Cancelled   => "the request was cancelled",
        LspRequestStatus.Unavailable => "the language server is not available",
        _                            => "the request succeeded",
    };

    internal static int? ErrorCodeOf(JObject? error) =>
        error?["code"]?.Type == JTokenType.Integer ? error["code"]!.Value<int>() : null;
}

/// <summary>An owned request failed for a reason other than ContentModified or caller cancellation.</summary>
internal sealed class LspRequestFailedException : InvalidOperationException
{
    public LspRequestFailedException(string method, string documentUri, LspRequestStatus status, JObject? error)
        : base(BuildMessage(method, documentUri, status, error))
    {
        Method = method;
        DocumentUri = documentUri;
        Status = status;
        Error = error;
    }

    /// <summary>The LSP method that failed.</summary>
    public string Method { get; }

    /// <summary>The document (or other subject) the request was for.</summary>
    public string DocumentUri { get; }

    /// <summary>How the request ended.</summary>
    public LspRequestStatus Status { get; }

    /// <summary>The server's JSON-RPC error, when it sent one.</summary>
    public JObject? Error { get; }

    private static string BuildMessage(string method, string documentUri, LspRequestStatus status, JObject? error) =>
        status == LspRequestStatus.ServerError
            ? $"{method} for {documentUri} failed: server error {error?["code"]}: {error?["message"]}."
            : $"{method} for {documentUri} failed: {status} (no response).";
}
