using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Shell;
using Reqnroll.IdeSupport.VisualStudio;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.LspNotifications;

/// <summary>
/// Pushes the current solution's project state to the LSP server's <c>ProjectPreloadListener</c>
/// side channel as soon as the DTE solution is loaded — independent of whether VS has activated
/// the <c>LanguageServerProvider</c> (i.e. opened a <c>.feature</c> file) yet.
/// </summary>
/// <remarks>
/// This is what lets binding discovery start running while the server is still sitting idle
/// waiting for VS's real <c>initialize</c> handshake (see docs/LSP-IDE-Support-Architecture.md's
/// As-built note on eager server startup). The real <see cref="VsProjectEventMonitor"/>, created
/// later in <c>ReqnrollLanguageClient.OnServerInitializationResultAsync</c>, re-sends the same
/// baseline over the normal LSP channel — <c>ILspWorkspaceScopeManager.HandleProjectLoadedAsync</c>/
/// <c>HandleProjectFilesAsync</c> treat a repeat baseline for an already-loaded project as an
/// update, not a duplicate, so this is safe to race with (whichever arrives first wins the initial
/// discovery; the second is a cheap no-op/refresh).
/// </remarks>
internal static class LspProjectPreloadPusher
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Waits for the solution to be loaded, then pushes a <c>reqnroll/projectLoaded</c> +
    /// <c>reqnroll/projectFiles</c> baseline for every solution project to the preload pipe
    /// named after <paramref name="serverProcessId"/>. Best-effort: any failure is logged and
    /// swallowed — the real <see cref="VsProjectEventMonitor"/> path is the fallback of record.
    /// </summary>
    public static async Task PushAsync(int serverProcessId, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var solution = await WaitForSolutionAsync(cancellationToken).ConfigureAwait(false);
            if (solution is null)
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var serviceProvider = ServiceProvider.GlobalProvider;

            var pipeName = $"reqnroll-preload-{serverProcessId}";
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15000, cancellationToken).ConfigureAwait(false);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            foreach (Project project in solution.Projects)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (!VsUtils.IsSolutionProject(project))
                    continue;

                var loadedJson = VsProjectPayloadBuilder.BuildProjectLoadedParamsJson(
                    project, GetSolutionFolder(solution), serviceProvider, logger);
                var filesJson = VsProjectPayloadBuilder.BuildProjectFilesParamsJson(project, logger);

                await WriteEnvelopeAsync(pipe, "reqnroll/projectLoaded", loadedJson, cancellationToken)
                    .ConfigureAwait(false);
                await WriteEnvelopeAsync(pipe, "reqnroll/projectFiles", filesJson, cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogInformation("LspProjectPreloadPusher: pushed initial project state to preload pipe.");
        }
        catch (OperationCanceledException) { /* extension shutting down or pipe never appeared in time */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LspProjectPreloadPusher: failed to push preload data.");
        }
    }

    private static async Task<Solution?> WaitForSolutionAsync(CancellationToken cancellationToken)
    {
        // The solution is very often not yet loaded when the eager server-connection service
        // starts (OnInitializedAsync can fire seconds before SolutionEvents.Opened). Poll rather
        // than subscribing to the event: this method's lifetime is short and one-shot, so a
        // dedicated event-handler subscription (with its own dispose lifecycle) is unwarranted.
        for (var i = 0; i < 120; i++) // up to ~60s
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var serviceProvider = ServiceProvider.GlobalProvider;
            var dteService = serviceProvider.GetService(typeof(DTE));
            if (dteService is DTE2 dte && dte.Solution?.IsOpen == true)
                return dte.Solution;

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string GetSolutionFolder(Solution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var solutionFile = solution.FullName;
        return string.IsNullOrEmpty(solutionFile)
            ? string.Empty
            : Path.GetDirectoryName(solutionFile) ?? string.Empty;
    }

    private static async Task WriteEnvelopeAsync(
        NamedPipeClientStream pipe, string method, string paramsJson, CancellationToken cancellationToken)
    {
        var line  = $"{{\"method\":{JsonEscape(method)},\"params\":{paramsJson}}}\n";
        var bytes = Utf8NoBom.GetBytes(line);
        await pipe.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string JsonEscape(string value) => Newtonsoft.Json.JsonConvert.ToString(value);
}
