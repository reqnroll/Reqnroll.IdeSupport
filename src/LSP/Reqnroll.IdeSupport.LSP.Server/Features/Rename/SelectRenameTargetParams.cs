#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>Parameters for reqnroll/selectRenameTarget notification (Step Rename refactoring).</summary>
public sealed class SelectRenameTargetParams
{
    // DocumentUri (not string): every other URI-keyed lookup in this codebase (MatchSetKey,
    // DocumentBufferService) sources both the write-key and read-key from a DocumentUri.ToString()
    // round-trip, which is why they never see client URI-encoding quirks (e.g. VS Code's
    // Uri.toString() percent-encoding the Windows drive-letter colon). A raw string field here
    // was the actual root cause of a bug where RenameSessionManager's key from this notification
    // never matched HandleRenameAsync's DocumentUri-derived key for real client traffic — typing
    // this the same way the rest of the codebase does removes the mismatch at its source, rather
    // than relying solely on string-normalization to paper over it.
    /// <summary>Gets or sets the uri.</summary>
    [JsonProperty("uri")]
    public DocumentUri Uri { get; set; } = null!;

    /// <summary>Gets or sets the version.</summary>
    [JsonProperty("version")]
    public int Version { get; set; }

    /// <summary>Gets or sets the attribute index.</summary>
    [JsonProperty("attributeIndex")]
    public int AttributeIndex { get; set; }

    /// <summary>
    /// The position the disambiguation was invoked at — the same one passed to
    /// <c>reqnroll/renameTargets</c> — so the server can re-derive that candidate list now and
    /// record <i>which</i> binding <see cref="AttributeIndex"/> denoted, rather than storing a bare
    /// index whose meaning depends on a list rebuilt later (issue #671, R5; see
    /// <c>RenameBindingIdentity</c>).
    /// </summary>
    /// <remarks>
    /// Optional: a client that omits it leaves the server with only the index, which is the
    /// pre-R5 behaviour rather than a failure.
    /// </remarks>
    [JsonProperty("position")]
    public Position? Position { get; set; }
}
