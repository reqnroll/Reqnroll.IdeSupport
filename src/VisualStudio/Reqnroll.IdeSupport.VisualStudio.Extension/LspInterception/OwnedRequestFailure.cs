#nullable enable

using System;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Turns a failed owned request (see <see cref="LspInterceptingPipe.SendRequestToServerWithErrorAsync"/>)
/// into an exception, for callers whose result is cached or otherwise reused (issue #1017).
/// </summary>
/// <remarks>
/// The pipe reports a server error, the 60 s timeout, a terminated server, a disposed pipe and
/// cancellation all as a <see langword="null"/> result — and a server that answered with JSON
/// <c>null</c> looks the same. A caller may therefore treat <see langword="null"/> as a failure only
/// for a method whose handler never answers with JSON <c>null</c> (<c>textDocument/codeLens</c>
/// answers an array, <c>reqnroll/resolveTestTargets</c> and <c>reqnroll/resolveContainerTestTargets</c>
/// an object). Throwing rather than returning "empty" is what keeps the failure out of the cache:
/// a faulted entry is not reused, whereas an empty list that "succeeded" would be served until the
/// next explicit invalidation.
/// </remarks>
internal static class OwnedRequestFailure
{
    /// <summary>
    /// Creates the exception to throw for a request that produced no result: an
    /// <see cref="LspContentModifiedException"/> for ContentModified (callers that restart on it,
    /// like the Run CodeLens cache, recognise it), otherwise an <see cref="InvalidOperationException"/>.
    /// </summary>
    public static Exception Create(string method, string documentUri, JObject? error)
    {
        if (error?["code"]?.Type == JTokenType.Integer &&
            error["code"]!.Value<int>() == LspContentModifiedException.ErrorCode)
            return new LspContentModifiedException(method, documentUri);

        var reason = error is null
            ? "no response (timed out, cancelled, or the server is unavailable)"
            : $"server error {error["code"]}: {error["message"]}";
        return new InvalidOperationException($"{method} for {documentUri} failed: {reason}.");
    }
}
