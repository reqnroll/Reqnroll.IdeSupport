#nullable enable

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Reqnroll.IdeSupport.VisualStudio.Extension.Activation;

namespace Reqnroll.IdeSupport.VisualStudio.Extension.Documents;

/// <summary>
/// Realizes the text buffers of restored <c>.feature</c> stub frames in the VS Running Document
/// Table (RDT) so the LSP's <c>textDocument/didOpen</c> fires for them once the server starts.
/// </summary>
/// <remarks>
/// Called only from <see cref="ReqnrollLanguageClient.OnServerInitializationResultAsync"/>
/// (post-server-init flush), after VS's real LSP handshake has already completed for whichever
/// feature tab triggered activation. All public methods must be called from the UI thread.
/// <para>
/// This is deliberately <b>passive</b>: it never forces the <c>LanguageServerProvider</c> to
/// activate. An earlier "invisible open" that force-activated the provider was removed — it raced
/// with VS's own restore of feature tabs and bounced the provider (two server processes, flickering
/// C# code lenses, broken feature state). The provider activates the normal way: when VS realizes a
/// restored feature tab, or the user opens a feature file. Moving this call any earlier (e.g. to
/// mirror the eager server-startup work in <c>ExtensionEntrypoint.OnInitializedAsync</c>) would
/// reintroduce that race.
/// </para>
/// <para>
/// The two-server bounce itself came from <c>LspServerConnectionService.GetConnectionAsync</c>
/// returning the same pipe to a second <c>CreateServerConnectionAsync</c> call. Since issue #156 it
/// returns a fresh pipe per call over one server process, so that failure no longer applies.
/// <see cref="ScratchFileActivationTrigger"/> (issue #533) now recovers a missed activation, but
/// it does so by opening a separate scratch file, never by touching the user's documents.
/// </para>
/// </remarks>
internal static class VsStubFrameInitializer
{
    /// <summary>
    /// Forces initialization of <c>.feature</c> stub frames discovered via RDT scan. Called after
    /// the LSP server initialises to flush remaining background stubs.
    /// </summary>
    public static async Task ForceInitFeatureStubsAsync(
        IServiceProvider serviceProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        var rdt = serviceProvider.GetService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
        if (rdt != null)
            TryForceInitRdtStubs(rdt, serviceProvider, logger);
    }

    // ── RDT stub scan ───────────────────────────────────────────────────────

    private static bool TryForceInitRdtStubs(
        IVsRunningDocumentTable rdt,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        rdt.GetRunningDocumentsEnum(out var enumDocs);
        if (enumDocs is null)
            return false;

        var cookies = new uint[1];
        var anyFound = false;

        while (enumDocs.Next(1, cookies, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            var cookie = cookies[0];

            rdt.GetDocumentInfo(cookie, out _, out _, out _, out var moniker, out _, out _, out var docData);

            if (moniker is null || !moniker.EndsWith(".feature", StringComparison.OrdinalIgnoreCase))
                continue;

            anyFound = true;

            // If document data is already initialized, skip.
            if (docData != IntPtr.Zero)
            {
                logger.LogDebug(
                    "VsStubFrameInitializer: {Moniker} is already initialized — skipping.", moniker);
                continue;
            }

            // Force-initialize: use IsDocumentOpen to get the window frame, then
            // request its DocData property. This triggers VS to fully initialize the
            // document (text buffer, content type, etc.) which in turn fires
            // textDocument/didOpen to the LSP client.
            if (VsShellUtilities.IsDocumentOpen(serviceProvider, moniker, Guid.Empty,
                    out var hier, out _, out var frame))
            {
                logger.LogDebug(
                    "VsStubFrameInitializer: forcing init of stub {Moniker} via window frame.", moniker);
                _ = frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var _);

                // After initialization, ensure the document has a real project hierarchy
                // (not the miscellaneous-files bucket).  If it doesn't, reopen via DTE
                // which registers the document with the owning project's IVsHierarchy.
                if (hier == null || IsMiscellaneousFilesProject(hier, serviceProvider, logger))
                {
                    try
                    {
                        var dte = serviceProvider.GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                        if (dte != null)
                        {
                            logger.LogDebug(
                                "VsStubFrameInitializer: reopening {Moniker} through DTE for project context.", moniker);
                            dte.ItemOperations.OpenFile(moniker);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "VsStubFrameInitializer: could not set project context for {Moniker}", moniker);
                    }
                }
            }
        }

        return anyFound;
    }

    private static bool IsMiscellaneousFilesProject(
        IVsHierarchy hier, IServiceProvider serviceProvider, ILogger logger)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var projectIdHr = hier.GetGuidProperty(
                VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_ProjectIDGuid, out var projectId);
            var typeGuidHr = hier.GetGuidProperty(
                VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_TypeGuid, out var typeGuid);

            var match = MiscellaneousFilesProject.MatchByGuid(projectIdHr, projectId, typeGuidHr, typeGuid);
            if (match == MiscellaneousFilesMatch.None && IsExternalFilesProject(hier, serviceProvider))
                match = MiscellaneousFilesMatch.ExternalFilesProjectIdentity;

            // The decision still rests on the ProjectIDGuid comparison alone, as it always has. It
            // was never verified against a running VS, but acting on the other signals would newly
            // reopen (and activate) loose .feature tabs through DTE, so they are only logged until a
            // live session shows which signal VS actually reports for the Misc Files hierarchy.
            if (match is not MiscellaneousFilesMatch.None and not MiscellaneousFilesMatch.ProjectIdGuid)
            {
                logger.LogInformation(
                    "VsStubFrameInitializer: hierarchy is Misc Files by {Match} but not by ProjectIDGuid ({ProjectIdHr:X8} {ProjectId}); not reopening.",
                    match, projectIdHr, projectId);
            }
            else
            {
                logger.LogDebug(
                    "VsStubFrameInitializer: Misc Files check = {Match} (ProjectIDGuid {ProjectIdHr:X8} {ProjectId}, TypeGuid {TypeGuidHr:X8} {TypeGuid}).",
                    match, projectIdHr, projectId, typeGuidHr, typeGuid);
            }

            return match == MiscellaneousFilesMatch.ProjectIdGuid;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// <see langword="true"/> when <paramref name="hier"/> is the same COM object as the shell's
    /// Miscellaneous Files project, obtained from the documented <c>SVsExternalFilesManager</c>.
    /// </summary>
    private static bool IsExternalFilesProject(IVsHierarchy hier, IServiceProvider serviceProvider)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (serviceProvider.GetService(typeof(SVsExternalFilesManager)) is not IVsExternalFilesManager manager
            || ErrorHandler.Failed(manager.GetExternalFilesProject(out var externalFilesProject))
            || externalFilesProject is null)
            return false;

        var left = Marshal.GetIUnknownForObject(hier);
        var right = Marshal.GetIUnknownForObject(externalFilesProject);
        try
        {
            return left == right;
        }
        finally
        {
            Marshal.Release(left);
            Marshal.Release(right);
        }
    }
}
