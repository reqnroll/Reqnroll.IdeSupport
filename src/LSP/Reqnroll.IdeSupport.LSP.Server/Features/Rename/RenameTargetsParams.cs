#nullable enable

using Newtonsoft.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Rename;

/// <summary>
/// Parameters for <c>reqnroll/renameTargets</c>. Extends the standard
/// <see cref="TextDocumentPositionParams"/> shape with an optional strictness flag (issue #506).
/// </summary>
/// <remarks>
/// <see cref="RequireAttributeLine"/> is set by VS Code's F2 dispatcher, which must not steal an
/// ordinary C# rename-symbol away from a method or local identifier. Without it, the cursor being
/// anywhere on a bound method's declaration line — not just within the binding attribute itself —
/// counts as a match (see <see cref="RenameBindingResolver"/>'s method-identifier-line fallback),
/// which is the intended, deliberately loose behavior for the explicit "Reqnroll: Rename Step"
/// context-menu/palette command but hijacks F2 on the method name away from the native C# rename
/// provider. When true, only the binding's own attribute line counts.
/// </remarks>
public sealed record RenameTargetsParams : TextDocumentPositionParams
{
    [JsonProperty("requireAttributeLine")]
    public bool RequireAttributeLine { get; set; }
}
