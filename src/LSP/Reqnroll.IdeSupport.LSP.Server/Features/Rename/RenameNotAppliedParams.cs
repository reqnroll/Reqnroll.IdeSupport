#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Parameters for the <c>reqnroll/renameNotApplied</c> notification: the server telling Visual
/// Studio that the rename edit it pushed with <c>workspace/applyEdit</c> was not applied.
/// </summary>
/// <remarks>
/// Visual Studio receives the rename response before the edit is pushed (see
/// <see cref="RenamePostApplyCoordinator.SchedulePostResponseApply"/>), so by the time the push
/// fails there is no request left to fail. This notification is how the failure reaches the user.
/// </remarks>
public sealed class RenameNotAppliedParams
{
    /// <summary>Gets or sets the URI of the document the rename was invoked on.</summary>
    [JsonProperty("uri")]
    public DocumentUri Uri { get; set; } = null!;

    /// <summary>Gets or sets the message to show the user.</summary>
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
}
