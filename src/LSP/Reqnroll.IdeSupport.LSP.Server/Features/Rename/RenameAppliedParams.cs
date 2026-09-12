#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Parameters for the <c>reqnroll/renameApplied</c> notification: the client reporting whether it
/// actually applied the <see cref="OmniSharp.Extensions.LanguageServer.Protocol.Models.WorkspaceEdit"/>
/// returned from <c>textDocument/rename</c> (issue #671, R3).
/// </summary>
/// <remarks>
/// Without this, the server had to assume every non-Visual-Studio client applied the edit it was
/// handed, and committed the corresponding registry/match-cache updates unconditionally. Rider
/// disproved that: its pre-apply staleness check can discard the edit after the rename response
/// has already been sent, which left the binding registry describing a step expression present in
/// no file (issue #670). Clients that apply the edit themselves must send this either way — with
/// <see cref="Applied"/> <see langword="false"/> when they discarded it, so the server can drop the
/// staged updates promptly rather than holding them indefinitely.
/// </remarks>
public sealed class RenameAppliedParams
{
    // DocumentUri (not string), matching SelectRenameTargetParams — see its remarks for the
    // URI-encoding key-mismatch bug that convention exists to prevent. This must round-trip to the
    // same key RenameHandler.HandleRenameAsync staged the pending commit under.
    /// <summary>Gets or sets the URI of the document the rename was invoked on.</summary>
    [JsonProperty("uri")]
    public DocumentUri Uri { get; set; } = null!;

    /// <summary>Gets or sets a value indicating whether the client applied the edit.</summary>
    [JsonProperty("applied")]
    public bool Applied { get; set; }
}
