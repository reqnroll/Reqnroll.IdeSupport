using Newtonsoft.Json;

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

/// <summary>
/// The three notifications a client uses to keep the server's workspace/project index in sync:
/// <c>reqnroll/projectLoaded</c>, <c>reqnroll/projectUnloaded</c>, <c>reqnroll/projectFiles</c>.
/// </summary>
public sealed class ReqnrollWorkspaceLifecycleOptions
{
    [JsonProperty("projectLoadedMethod")]
    public required string ProjectLoadedMethod { get; init; }

    [JsonProperty("projectUnloadedMethod")]
    public required string ProjectUnloadedMethod { get; init; }

    [JsonProperty("projectFilesMethod")]
    public required string ProjectFilesMethod { get; init; }
}

/// <summary>
/// The three-method step-rename refactoring workflow: <c>reqnroll/renameTargets</c> (disambiguation
/// candidates for a multi-binding match), <c>reqnroll/selectRenameTarget</c> (the client's chosen
/// candidate), <c>reqnroll/renameApplied</c> (post-apply confirmation). Runs alongside the standard
/// <c>textDocument/prepareRename</c> + <c>textDocument/rename</c>, which handle the common
/// single-candidate case unassisted.
/// </summary>
public sealed class ReqnrollStepRenameOptions
{
    [JsonProperty("renameTargetsMethod")]
    public required string RenameTargetsMethod { get; init; }

    [JsonProperty("selectRenameTargetMethod")]
    public required string SelectRenameTargetMethod { get; init; }

    [JsonProperty("renameAppliedMethod")]
    public required string RenameAppliedMethod { get; init; }
}
