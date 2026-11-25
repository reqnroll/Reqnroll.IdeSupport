#nullable enable

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.VisualStudio.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Writes the server's <c>reqnroll/logMessage</c> notifications (problems the user can act on, such
/// as a failed binding discovery) to the "Reqnroll" Output Window pane through
/// <see cref="ExtensionHostLogger"/>. The server sends these instead of <c>window/logMessage</c>,
/// which Visual Studio shows in its generic "Reqnroll Language Client" pane.
/// </summary>
internal sealed class ServerLogMessageInterceptor : ILspMessageInterceptor
{
    private readonly IIdeSupportLogger _paneLogger;

    public ServerLogMessageInterceptor(IIdeSupportLogger? paneLogger = null)
    {
        _paneLogger = paneLogger ?? ExtensionHostLogger.Instance;
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var body = message.Body;
        var method = body?["method"]?.Value<string>();
        if (!string.Equals(method, CustomLspMethodNames.ReqnrollLogMessage, System.StringComparison.Ordinal))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var @params = body!["params"] as JObject;
        var text = @params?["message"]?.Value<string>();
        if (!string.IsNullOrEmpty(text))
        {
            var type = @params?["type"]?.Type == JTokenType.Integer ? @params["type"]!.Value<int>() : 0;
            _paneLogger.Log(new LogMessage(ToTraceLevel(type), text!, nameof(InterceptAsync), Source: "Language server"));
        }

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    /// <summary>Maps an LSP <c>MessageType</c> (1 Error, 2 Warning, 3 Info, 4 Log) onto a <see cref="TraceLevel"/>.</summary>
    internal static TraceLevel ToTraceLevel(int messageType) => messageType switch
    {
        1 => TraceLevel.Error,
        2 => TraceLevel.Warning,
        3 => TraceLevel.Info,
        _ => TraceLevel.Verbose,
    };
}
