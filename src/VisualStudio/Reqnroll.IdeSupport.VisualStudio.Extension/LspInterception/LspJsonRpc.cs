using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Pure JSON-RPC shape classifiers and message builders (issue #587, step 2). No session state, no
/// I/O: everything here is a function of the <see cref="JObject"/> or strings passed in, which is
/// what lets the correlation and routing rules that depend on these be tested without standing up a
/// pipe.
/// </summary>
/// <remarks>
/// The distinctions matter and are easy to get subtly wrong, so they are stated once here rather
/// than re-derived at each call site:
/// a <b>request</b> has both <c>method</c> and <c>id</c>; a <b>notification</b> has <c>method</c> and
/// no <c>id</c>; a <b>response</b> has <c>id</c> and no <c>method</c>.
/// </remarks>
internal static class LspJsonRpc
{
    /// <summary>
    /// True if <paramref name="body"/> is the LSP <c>exit</c> notification — a notification (no
    /// <c>id</c>) whose method is <c>exit</c>. Per the spec this asks the server to terminate its
    /// process, so it is the definitive end-of-session marker on the VS → server direction.
    /// </summary>
    /// <remarks>
    /// An <c>id</c> present but JSON-null counts as absent: <c>JObject["id"]</c> returns a
    /// <see cref="JTokenType.Null"/> token for <c>"id":null</c>, not a C# <see langword="null"/>, so
    /// testing for the latter alone would miss such a frame and leave the connection looking alive
    /// after its server had been told to leave — the whole failure this detection exists to prevent
    /// (issue #555).
    /// </remarks>
    public static bool IsExitNotification(JObject body) =>
        (body["id"] is null || body["id"]!.Type == JTokenType.Null) &&
        string.Equals(body["method"]?.Value<string>(), "exit", StringComparison.Ordinal);

    /// <summary>
    /// True if <paramref name="body"/> is a JSON-RPC <b>request</b> (has both <c>id</c> and
    /// <c>method</c>). Used to record which VS-facing session sent each request (issue #395).
    /// </summary>
    public static bool TryGetRequestId(JObject body, out string id)
    {
        id = string.Empty;
        if (!body.ContainsKey("method")) return false;

        var idToken = body["id"];
        var idValue = idToken?.Value<string>();
        if (idValue is null) return false;

        id = idValue;
        return true;
    }

    /// <summary>
    /// True if <paramref name="body"/> is a JSON-RPC <b>response</b> (has <c>id</c>, no
    /// <c>method</c>).
    /// </summary>
    public static bool TryGetResponseId(JObject body, out string id)
    {
        id = string.Empty;
        if (body.ContainsKey("method")) return false;

        var idToken = body["id"];
        var idValue = idToken?.Value<string>();
        if (idValue is null) return false;

        id = idValue;
        return true;
    }

    /// <summary>Builds a JSON-RPC notification body. <paramref name="paramsJson"/> is already-serialized JSON, or null/empty to omit the field.</summary>
    public static string BuildNotification(string method, string? paramsJson) =>
        string.IsNullOrEmpty(paramsJson)
            ? $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonEscape(method)}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}";

    /// <summary>Builds a JSON-RPC request body. <paramref name="paramsJson"/> is already-serialized JSON, or null/empty to omit the field.</summary>
    public static string BuildRequest(string id, string method, string? paramsJson) =>
        string.IsNullOrEmpty(paramsJson)
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonEscape(id)},\"method\":{JsonEscape(method)}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{JsonEscape(id)},\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}";

    /// <summary>Quotes and escapes <paramref name="value"/> as a JSON string literal (including the surrounding quotes).</summary>
    public static string JsonEscape(string value) => JsonConvert.ToString(value);
}
