#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Lsp;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Shows the server's <c>reqnroll/renameNotApplied</c> notification to the user: the rename edit the
/// server pushed with <c>workspace/applyEdit</c> was rejected by Visual Studio (for example because a
/// file is read-only or locked), so the step was not renamed.
/// </summary>
/// <remarks>
/// The server answers the rename request before it pushes the edit, so the Rename Step command has
/// already returned by the time the push fails. The message is shown in the same dialog as a rename the
/// server refused (<see cref="EditRejection"/>). The dialog is modal, so it is shown off the receive
/// pump: waiting for the user to close it would hold up every message behind this one.
/// </remarks>
internal sealed class RenameNotAppliedInterceptor : ILspMessageInterceptor
{
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Task>? _show;

    /// <param name="logger">Logger for the notification and for a failure to show it.</param>
    /// <param name="show">Test seam passed on to <see cref="EditRejection.ReportAsync"/>.</param>
    public RenameNotAppliedInterceptor(ILogger<RenameNotAppliedInterceptor> logger, Func<string, CancellationToken, Task>? show = null)
    {
        _logger = logger;
        _show   = show;
    }

    /// <summary>The task showing the most recent message; lets tests wait for it.</summary>
    internal Task LastReport { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public Task<LspInterceptorResult> InterceptAsync(LspMessage message, CancellationToken cancellationToken)
    {
        if (message.Direction != LspMessageDirection.Receive)
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var body = message.Body;
        var method = body?["method"]?.Value<string>();
        if (!string.Equals(method, CustomLspMethodNames.ReqnrollRenameNotApplied, StringComparison.Ordinal))
            return Task.FromResult(LspInterceptorResult.PassThrough);

        var text = (body!["params"] as JObject)?["message"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("RenameNotAppliedInterceptor: {Method} arrived without a message; nothing to show.", method);
            return Task.FromResult(LspInterceptorResult.PassThrough);
        }

        // CancellationToken.None: the dialog belongs to the user, not to this message's dispatch.
        LastReport = Task.Run(() => ReportAsync(text!));

        // Passed through like the other reqnroll/* notifications; Visual Studio's client ignores it.
        return Task.FromResult(LspInterceptorResult.PassThrough);
    }

    private async Task ReportAsync(string text)
    {
        try
        {
            await EditRejection.ReportAsync(_logger, "RenameNotAppliedInterceptor", text, CancellationToken.None, _show)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameNotAppliedInterceptor: could not show the message: {Message}", text);
        }
    }
}
