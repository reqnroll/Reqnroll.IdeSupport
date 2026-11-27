using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Core.Discovery;
using Reqnroll.IdeSupport.LSP.Core.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Tells the client, once per server process, that the workspace contains a project that uses
/// Reqnroll, by sending <see cref="CustomLspMethodNames.ReqnrollProjectDetected"/>. Clients hold
/// their telemetry until then, so an IDE that loads the extension for a solution without Reqnroll
/// sends only errors, server start failures, and install or upgrade events.
/// </summary>
/// <remarks>
/// <para>
/// Only the server can answer the question: every client reports every project it knows about
/// (VS and VS Code the whole solution or workspace, Rider every runnable project), and the rule
/// for "uses Reqnroll" lives in <see cref="ReqnrollProjectDetector"/>. That detector is used here
/// without a feature-file lookup, so a Reqnroll binding library with no feature files of its own
/// counts too, and the <c>ide.reqnroll.isReqnrollProject</c> setting overrides it either way.
/// </para>
/// <para>
/// A project is checked when it is first reported and again whenever its binding registry is
/// replaced (<see cref="Pipeline.BindingRegistryReplacedNotification"/>). The second check is what
/// finds a Rider project: Rider sends no package references, so the detector only recognises it by
/// the <c>Reqnroll.dll</c> that appears next to the output assembly after a build.
/// </para>
/// <para>
/// A project can be reported before the LSP handshake has finished (Visual Studio pushes its
/// projects through a side channel while the server is starting), and a server must not send
/// notifications before then, so the notification waits for <see cref="OnServerStarted"/>.
/// </para>
/// </remarks>
public sealed class ReqnrollProjectPresenceNotifier
{
    private readonly IReqnrollProjectDetector _detector;
    private readonly Action _sendNotification;
    private readonly IIdeSupportLogger _logger;

    private int _detected;
    private int _started;
    private int _sent;

    /// <summary>Initializes a new instance that sends the notification through <paramref name="languageServer"/>.</summary>
    public ReqnrollProjectPresenceNotifier(
        ILspWorkspaceScopeManager scopeManager,
        IFileSystemForIDE fileSystem,
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger)
        : this(scopeManager, new ReqnrollProjectDetector(fileSystem),
            () => languageServer.SendNotification(CustomLspMethodNames.ReqnrollProjectDetected), logger)
    {
    }

    /// <summary>Test seam: supplies the detector and the notification sender.</summary>
    internal ReqnrollProjectPresenceNotifier(
        ILspWorkspaceScopeManager scopeManager,
        IReqnrollProjectDetector detector,
        Action sendNotification,
        IIdeSupportLogger logger)
    {
        _detector = detector;
        _sendNotification = sendNotification;
        _logger = logger;
        scopeManager.ProjectDiscovered += project => Evaluate(project);
    }

    /// <summary><see langword="true"/> once a project that uses Reqnroll has been seen.</summary>
    public bool IsDetected => Volatile.Read(ref _detected) == 1;

    /// <summary>Checks <paramref name="project"/> and notifies the client if it is the first that uses Reqnroll.</summary>
    public void Evaluate(IProjectScope project)
    {
        if (IsDetected)
            return;

        bool usesReqnroll;
        try
        {
            usesReqnroll = _detector.IsReqnrollTestProject(project);
        }
        catch (Exception ex)
        {
            // The check reads the project's configuration and probes its output folder; a failure
            // there must not break project registration, and the next registry change retries.
            _logger.LogVerbose($"Reqnroll project check failed for '{project.ProjectName}': {ex.Message}");
            return;
        }

        if (!usesReqnroll || Interlocked.Exchange(ref _detected, 1) == 1)
            return;

        _logger.LogInfo($"First Reqnroll project detected: {project.ProjectName}");
        TrySend();
    }

    /// <summary>Marks the LSP handshake as complete and sends the notification if it is already due.</summary>
    public void OnServerStarted()
    {
        // A full fence, like the one on _detected, so this and Evaluate on another thread cannot
        // each miss the other's flag and leave the notification unsent.
        Interlocked.Exchange(ref _started, 1);
        TrySend();
    }

    private void TrySend()
    {
        if (Volatile.Read(ref _started) == 0 || !IsDetected || Interlocked.Exchange(ref _sent, 1) == 1)
            return;

        try
        {
            _sendNotification();
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"{CustomLspMethodNames.ReqnrollProjectDetected} failed: {ex.Message}");
        }
    }
}
