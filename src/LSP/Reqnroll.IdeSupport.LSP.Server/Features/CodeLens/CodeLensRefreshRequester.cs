using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
namespace Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;

/// <summary>
/// Asks the client to invalidate its rendered C# step code lenses: <c>reqnroll/refreshCodeLens</c>
/// for Visual Studio (whose client-side <c>StepCodeLensService</c> uses the <c>LspInterceptingPipe</c>
/// directly rather than VS's built-in LSP code-lens infrastructure, so it cannot route the standard
/// request), or <c>workspace/codeLens/refresh</c> for every other client.
/// </summary>
/// <remarks>
/// Shared by <see cref="Pipeline.BindingRegistryChangedHandler"/> (refresh after a .cs/connector
/// binding-registry change) and <see cref="Pipeline.CodeLensRefreshHandler"/> (refresh after a
/// .feature file edit changes a step's usage count) so the VS/non-VS branching lives in one place.
/// </remarks>
internal static class CodeLensRefreshRequester
{
    /// <summary>Sends the appropriate refresh notification/request for the connected client. <paramref name="projectName"/> is informational only (see <see cref="RefreshCodeLensParams.ProjectName"/>) and may be empty. <paramref name="isFullReplacement"/> is passed through to <see cref="RefreshCodeLensParams.IsFullReplacement"/> so the VS client can skip the reconnect-risking invalidation on incremental refreshes (issue #156/#318). <paramref name="cancellationToken"/> is honoured only by the non-VS <c>workspace/codeLens/refresh</c> request (VS's path is a fire-and-forget notification); pass a caller-owned token -- e.g. a debouncer's token (issue #471) -- rather than <see cref="CancellationToken.None"/> so a superseding refresh can actually cancel one still in flight.</summary>
    public static async Task RequestRefreshAsync(
        ILanguageServerFacade languageServer,
        ClientIdeContext clientIde,
        IIdeSupportLogger logger,
        string projectName,
        bool isFullReplacement = false,
        CancellationToken cancellationToken = default)
    {
        if (clientIde.IsVisualStudio)
        {
            logger.LogInfo(
                $"Sending reqnroll/refreshCodeLens for project '{projectName}' " +
                $"(isFullReplacement={isFullReplacement}).");
            try
            {
                languageServer.SendNotification(
                    LspMethodNames.ReqnrollRefreshCodeLens,
                    new RefreshCodeLensParams { ProjectName = projectName, IsFullReplacement = isFullReplacement });
            }
            catch (Exception ex)
            {
                logger.LogWarning($"reqnroll/refreshCodeLens failed: {ex.Message}");
            }
            return;
        }

        logger.LogInfo($"Sending workspace/codeLens/refresh for project '{projectName}'.");
        try
        {
            await languageServer.Client
                .SendRequest(LspMethodNames.WorkspaceCodeLensRefresh)
                .ReturningVoid(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning($"workspace/codeLens/refresh failed: {ex.Message}");
        }
    }
}
