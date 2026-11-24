using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.Lsp;

namespace Reqnroll.IdeSupport.LSP.Server.Logging;

/// <summary>
/// Writes a message to the user's Reqnroll output pane in the IDE (the "Reqnroll" Output Window
/// pane in Visual Studio, the "Reqnroll" output channel in VS Code, the Reqnroll tool window in
/// Rider), for problems the user can act on. Everything else belongs in the server log only.
/// </summary>
public interface IClientOutputLog
{
    /// <summary>Sends <paramref name="message"/> to the client's Reqnroll output pane as a warning.</summary>
    void Warning(string message);
}

/// <summary>
/// <see cref="IClientOutputLog"/> over the <c>reqnroll/logMessage</c> notification. The standard
/// <c>window/logMessage</c> is not used because every client routes it to a generic
/// language-server channel that also carries the LSP library's own diagnostics.
/// </summary>
public sealed class ClientOutputLog : IClientOutputLog
{
    private readonly ILanguageServerFacade _languageServer;
    private readonly IIdeSupportLogger _logger;

    /// <summary>Initializes a new instance of the <see cref="ClientOutputLog"/> class.</summary>
    public ClientOutputLog(ILanguageServerFacade languageServer, IIdeSupportLogger logger)
    {
        _languageServer = languageServer;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Warning(string message) => Send(MessageType.Warning, message);

    private void Send(MessageType type, string message)
    {
        try
        {
            _languageServer.SendNotification(
                CustomLspMethodNames.ReqnrollLogMessage,
                new LogMessageParams { Type = type, Message = message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"{CustomLspMethodNames.ReqnrollLogMessage} failed: {ex.Message}");
        }
    }
}
