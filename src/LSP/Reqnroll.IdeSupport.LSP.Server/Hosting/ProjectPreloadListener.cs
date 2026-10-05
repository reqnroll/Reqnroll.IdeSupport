using System.IO.Pipes;
using Newtonsoft.Json.Linq;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Hosting;

/// <summary>
/// Listens on a process-local named pipe for <c>reqnroll/projectLoaded</c> and
/// <c>reqnroll/projectFiles</c> payloads sent by IDE glue <b>before</b> the LSP
/// <c>initialize</c> handshake completes, and dispatches them directly to
/// <see cref="ILspWorkspaceScopeManager"/>.
/// </summary>
/// <remarks>
/// OmniSharp's <c>LanguageServer.Initialize()</c> blocks until the client's real
/// <c>initialize</c> request is received, and defers/queues any other request or notification
/// routed through its own JSON-RPC dispatcher until then. This listener bypasses that dispatcher
/// entirely — it is started against <c>LanguageServer.PreInit(...).Services</c>, which is fully
/// constructed (DI container built, <see cref="ILspWorkspaceScopeManager"/> resolvable) before
/// <c>Initialize()</c> is ever called. See <see cref="ILspWorkspaceScopeManager.HandleProjectLoadedAsync"/>'s
/// own "auto-creating workspace scope" comment — the workspace/project model was already designed
/// to tolerate project notifications arriving before <c>initialize</c>'s workspace folders exist.
/// </remarks>
internal static class ProjectPreloadListener
{
    public static string PipeName(int processId) => $"reqnroll-preload-{processId}";

    /// <summary>
    /// Accepts preload connections until <paramref name="cancellationToken"/> is cancelled
    /// (the caller cancels once the real <c>initialize</c> handshake completes — the side
    /// channel has no further purpose after that point).
    /// </summary>
    /// <param name="pipeName">
    /// Defaults to <see cref="PipeName"/> of the current process; overridable for test isolation.
    /// </param>
    public static async Task RunAsync(
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger logger,
        CancellationToken cancellationToken,
        string? pipeName = null)
    {
        pipeName ??= PipeName(Environment.ProcessId);
        logger.LogInfo($"ProjectPreloadListener: listening on pipe '{pipeName}'.");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ProcessConnectionAsync(pipe, scopeManager, logger, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning($"ProjectPreloadListener: connection handling failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        finally
        {
            logger.LogInfo("ProjectPreloadListener: stopped.");
        }
    }

    private static async Task ProcessConnectionAsync(
        NamedPipeServerStream pipe,
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger logger,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var envelope    = JObject.Parse(line);
                var method      = envelope["method"]?.Value<string>();
                var paramsToken = envelope["params"];

                switch (method)
                {
                    case "reqnroll/projectLoaded":
                        var loaded = paramsToken?.ToObject<ReqnrollProjectLoadedParams>();
                        if (loaded is not null)
                            await scopeManager.HandleProjectLoadedAsync(loaded, cancellationToken)
                                .ConfigureAwait(false);
                        break;

                    case "reqnroll/projectFiles":
                        var files = paramsToken?.ToObject<ReqnrollProjectFilesParams>();
                        if (files is not null)
                            await scopeManager.HandleProjectFilesAsync(files, cancellationToken)
                                .ConfigureAwait(false);
                        break;

                    default:
                        logger.LogWarning($"ProjectPreloadListener: unknown preload method '{method}'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning($"ProjectPreloadListener: failed to process preload message: {ex.Message}");
            }
        }
    }
}
