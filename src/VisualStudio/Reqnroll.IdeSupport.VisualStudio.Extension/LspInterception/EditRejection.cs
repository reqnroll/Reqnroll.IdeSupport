#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.VisualStudio.Utilities;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspInterception;

/// <summary>
/// Client side of the server's "edit rejected" contract: classifies a JSON-RPC error and
/// presents a rejection to the user. One place, so each call site does not invent its own
/// status-bar/log combination.
/// </summary>
internal static class EditRejection
{
    /// <summary>
    /// If <paramref name="error"/> is a user-correctable rejection (code
    /// <see cref="EditRejectedException.ErrorCode"/> with a non-empty message), returns the
    /// exception to throw; otherwise <c>null</c> — a null/other-code error, including
    /// <c>ContentModified</c>, is not a rejection.
    /// </summary>
    public static EditRejectedException? TryCreateException(JObject? error)
    {
        if (error?["code"] is not { Type: JTokenType.Integer } code
            || code.Value<int>() != EditRejectedException.ErrorCode)
            return null;

        var message = error["message"]?.Value<string>();
        return string.IsNullOrWhiteSpace(message) ? null : new EditRejectedException(message!);
    }

    /// <summary>
    /// Logs the rejection and shows <paramref name="message"/> to the user in a modal dialog.
    /// <paramref name="show"/> is a test seam; it defaults to <see cref="VsUtils.ShowEditRejectedAsync"/>.
    /// </summary>
    public static async Task ReportAsync(
        ILogger logger, string source, string message, CancellationToken cancellationToken,
        System.Func<string, CancellationToken, Task>? show = null)
    {
        logger.LogInformation("{Source}: rejected by server: {Message}", source, message);
        await (show ?? VsUtils.ShowEditRejectedAsync)(message, cancellationToken).ConfigureAwait(false);
    }
}
