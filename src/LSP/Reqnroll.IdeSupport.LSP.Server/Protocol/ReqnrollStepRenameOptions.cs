using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Protocol;

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
