using MediatR;
namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Payload for the <c>reqnroll/projectUnloaded</c> client-to-server notification.
/// Sent by each IDE glue component when a Reqnroll project is removed from the
/// solution or the solution itself is closed.
/// </summary>
public sealed class ReqnrollProjectUnloadedParams : INotification
{
    /// <summary>
    /// Absolute path of the <c>.csproj</c> file that was unloaded.
    /// Must match the <see cref="ReqnrollProjectLoadedParams.ProjectFile"/> sent earlier.
    /// </summary>
    public string ProjectFile { get; set; } = string.Empty;
}
