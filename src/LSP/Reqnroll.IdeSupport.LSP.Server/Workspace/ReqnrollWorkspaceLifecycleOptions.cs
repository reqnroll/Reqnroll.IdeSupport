using Newtonsoft.Json;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

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
