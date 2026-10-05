using Newtonsoft.Json;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

/// <summary>
/// A single custom Reqnroll method, advertised as a typed top-level entry in
/// <c>ServerCapabilities.ExtensionData</c> rather than left undeclared. Every method here is
/// registered via manual <c>OnRequest</c>/<c>OnNotification</c> routing
/// (<see cref="LanguageServerOptionsExtensions.InitializeCustomProtocolRouting"/>), which bypasses
/// OmniSharp's <c>AddHandler</c>-driven dynamic capability registration entirely -- so without an
/// entry here, nothing in the <c>initialize</c> response says these methods exist at all.
///
/// This is documentation, not feature-detection: every one of these methods has existed since this
/// server's first version, so no client conditionally branches on whether one is present, the way a
/// client plausibly could for an incrementally-rolled-out feature. The value is (1) the
/// <c>initialize</c> response becomes a manifest of the server's full custom protocol surface a
/// developer can read directly, and (2) the matching spec scenarios in IdeProfiles.feature are a
/// cheap regression guard that a handler wasn't silently dropped from
/// <see cref="LanguageServerOptionsExtensions.InitializeCustomProtocolRouting"/>.
///
/// Reused for every single-method entry; a feature with more than one related method (workspace
/// lifecycle, step rename) gets its own small options type instead, grouping the related method
/// names together -- see <see cref="ReqnrollWorkspaceLifecycleOptions"/> and
/// <see cref="ReqnrollStepRenameOptions"/>.
/// </summary>
public sealed class ReqnrollMethodProvider
{
    [JsonProperty("method")]
    public required string Method { get; init; }
}
