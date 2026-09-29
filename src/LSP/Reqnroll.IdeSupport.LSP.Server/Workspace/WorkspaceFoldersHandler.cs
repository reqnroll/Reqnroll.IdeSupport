using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;
using Reqnroll.IdeSupport.LSP.Server.Performance;

namespace Reqnroll.IdeSupport.LSP.Server.Workspace;

/// <summary>
/// Handles <c>workspace/didChangeWorkspaceFolders</c> notifications.
/// Opens/closes <see cref="LspProjectScope"/> instances in response to the client
/// adding or removing workspace roots.
/// </summary>
public class WorkspaceFoldersHandler : IDidChangeWorkspaceFoldersHandler
{
    private readonly ILspWorkspaceScopeManager _scopeManager;
    private readonly IIdeSupportLogger _logger;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="WorkspaceFoldersHandler"/> class.</summary>
    public WorkspaceFoldersHandler(
        ILspWorkspaceScopeManager scopeManager,
        IIdeSupportLogger logger,
        IOperationDurationRecorder? recorder = null)
    {
        _scopeManager = scopeManager;
        _logger = logger;
        _recorder = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Handles <c>workspace/didChangeWorkspaceFolders</c> by opening a scope for each added folder and closing the scope for each removed folder.</summary>
    public Task<Unit> Handle(DidChangeWorkspaceFoldersParams request, CancellationToken cancellationToken)
    {
        using var _perf = _recorder.Measure(LspStandardMethodNames.WorkspaceDidChangeWorkspaceFolders);

        if (request.Event?.Added is not null)
        {
            foreach (var folder in request.Event.Added)
            {
                var path = folder.Uri.GetFileSystemPath();
                if (!string.IsNullOrEmpty(path))
                {
                    _logger.LogInfo($"Workspace folder added: {path}");
                    _scopeManager.OpenWorkspace(path);
                }
            }
        }

        if (request.Event?.Removed is not null)
        {
            foreach (var folder in request.Event.Removed)
            {
                var path = folder.Uri.GetFileSystemPath();
                if (!string.IsNullOrEmpty(path))
                {
                    _logger.LogInfo($"Workspace folder removed: {path}");
                    _scopeManager.CloseWorkspace(path);
                }
            }
        }

        return Unit.Task;
    }

    /// <summary>Builds the LSP registration options requesting workspace-folder change notifications from the client.</summary>
    public DidChangeWorkspaceFolderRegistrationOptions GetRegistrationOptions(ClientCapabilities clientCapabilities)
        => new() { ChangeNotifications = true };
}
