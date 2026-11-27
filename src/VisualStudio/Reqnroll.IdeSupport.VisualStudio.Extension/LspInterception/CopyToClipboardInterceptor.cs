#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.VisualStudio.Logging;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Puts the text of the server's <c>reqnroll/copyToClipboard</c> notifications on the Windows
/// clipboard. The server sends one when the user picks a "Copy step definition(s) to clipboard"
/// quick fix: LSP has no clipboard API, so the server relays the text back for the client to copy.
/// </summary>
/// <remarks>
/// The copy is started, not awaited, so the receive pipeline is never held up waiting for the UI
/// thread. Every message, this one included, passes through.
/// </remarks>
internal sealed class CopyToClipboardInterceptor : ILspMessageInterceptor
{
    private readonly Action<string> _copy;

    /// <param name="copy">Writes the text to the clipboard; defaults to <see cref="CopyOnUiThread"/>.</param>
    public CopyToClipboardInterceptor(Action<string>? copy = null)
    {
        _copy = copy ?? CopyOnUiThread;
    }

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var body = message.Body;
        var method = body?["method"]?.Value<string>();
        if (!string.Equals(method, CustomLspMethodNames.ReqnrollCopyToClipboard, StringComparison.Ordinal))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var textToken = (body!["params"] as JObject)?["text"];
        if (textToken?.Type == JTokenType.String && textToken.Value<string>() is { Length: > 0 } text)
            _copy(text);

        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the clipboard on the UI thread, which the WPF clipboard
    /// requires. Another process can hold the clipboard open, so a failed write is reported in the
    /// "Reqnroll" Output Window pane instead of being thrown.
    /// </summary>
    private static void CopyOnUiThread(string text)
    {
        var jtf = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory;
        _ = jtf.RunAsync(async () =>
        {
            await jtf.SwitchToMainThreadAsync();
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                ExtensionHostLogger.Instance.LogWarning($"Could not copy the step definition to the clipboard: {ex.Message}");
            }
        });
    }
}
