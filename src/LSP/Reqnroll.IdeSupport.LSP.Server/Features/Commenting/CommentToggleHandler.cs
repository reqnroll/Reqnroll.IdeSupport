using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Commenting;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Performance;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Features.Commenting;

/// <summary>
/// Handles <c>workspace/executeCommand</c> for <c>reqnroll.toggleComment</c> (Comment/Uncomment toggle).
/// Toggles <c>#</c> comments on the selected line(s) of a <c>.feature</c> file.
/// Arguments: <c>[uri, startLine, endLine, mode?]</c> (lines 0-based, inclusive). The optional
/// <c>mode</c> is <c>"toggle"</c> (default), <c>"comment"</c> or <c>"uncomment"</c> — see
/// <see cref="CommentToggleMode"/>.
/// Applies the resulting <see cref="WorkspaceEdit"/> via <c>workspace/applyEdit</c> request.
/// </summary>
public sealed class CommentToggleHandler : IExecuteCommandHandler
{
    private const string ToggleCommentCommand = "reqnroll.toggleComment";

    private readonly IDocumentBufferService   _documentBufferService;
    private readonly ICommentToggleService     _toggleService;
    private readonly ILanguageServerFacade     _languageServer;
    private readonly IIdeSupportLogger           _logger;
    private readonly ILspTelemetryService?     _telemetryService;
    private readonly IOperationDurationRecorder _recorder;

    /// <summary>Initializes a new instance of the <see cref="CommentToggleHandler"/> class.</summary>
    public CommentToggleHandler(
        IDocumentBufferService documentBufferService,
        ICommentToggleService toggleService,
        ILanguageServerFacade languageServer,
        IIdeSupportLogger logger,
        ILspTelemetryService? telemetryService = null,
        IOperationDurationRecorder? recorder = null)
    {
        _documentBufferService = documentBufferService;
        _toggleService         = toggleService;
        _languageServer        = languageServer;
        _logger                = logger;
        _telemetryService      = telemetryService;
        _recorder              = recorder ?? NullOperationDurationRecorder.Instance;
    }

    /// <summary>Builds the LSP registration options advertising the comment-toggle command as an executable <c>workspace/executeCommand</c> command.</summary>
    public ExecuteCommandRegistrationOptions GetRegistrationOptions(
        ExecuteCommandCapability capability,
        ClientCapabilities clientCapabilities)
        => new()
        {
            Commands = new Container<string>(ToggleCommentCommand)
        };

    /// <summary>Handles a <c>workspace/executeCommand</c> request for the comment-toggle command.</summary>
    public async Task<Unit> Handle(
        ExecuteCommandParams request,
        CancellationToken cancellationToken)
    {
        using var _perf = _recorder.Measure(ToggleCommentCommand);

        if (request.Command != ToggleCommentCommand)
        {
            _logger.LogVerbose($"CommentToggleHandler: unknown command '{request.Command}'");
            return Unit.Value;
        }

        var args = request.Arguments;
        if (args is null || args.Count < 3)
        {
            _logger.LogVerbose("CommentToggleHandler: missing arguments");
            return Unit.Value;
        }

        var uriStr    = args[0].Value<string>();
        var startLine = args[1].Value<int>();
        var endLine   = args[2].Value<int>();
        var modeArg   = args.Count > 3 ? args[3].Value<string>() : null;

        if (!TryParseMode(modeArg, out var mode))
        {
            _logger.LogVerbose($"CommentToggleHandler: unknown mode '{modeArg}'");
            return Unit.Value;
        }

        if (uriStr is null)
        {
            _logger.LogVerbose("CommentToggleHandler: null URI argument");
            return Unit.Value;
        }

        var uri = DocumentUri.Parse(uriStr);

        if (!_documentBufferService.TryGet(uri, out var buffer) || buffer is null)
        {
            _logger.LogVerbose($"CommentToggleHandler: buffer not found for {uri}");
            return Unit.Value;
        }

        var text   = buffer.Text;
        var lines  = text.Replace("\r\n", "\n").Split('\n');
        var result = _toggleService.ToggleComment(text, startLine, endLine, mode);

        var edit = BuildWorkspaceEdit(uri, result, lines);
        _logger.LogInfo($"Comment/Uncomment toggle reqnroll.toggleComment: {uri} lines [{startLine}..{endLine}] mode={mode} → {result.Edits.Count} change(s)");

        // Telemetry
        _telemetryService?.SendEvent(TelemetryEvents.CommentUncommentCommandExecuted, new());

        // Uncomment on lines that carry no comment yields nothing to change; skip the round-trip.
        if (result.Edits.Count == 0)
            return Unit.Value;

        await _languageServer.SendRequest(LspMethodNames.WorkspaceApplyEdit, edit)
            .Returning<ApplyWorkspaceEditResponse>(cancellationToken);

        return Unit.Value;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // A missing or null mode means Toggle, so clients that predate the argument keep working.
    private static bool TryParseMode(string? modeArg, out CommentToggleMode mode)
    {
        switch (modeArg)
        {
            case null:
            case "toggle":
                mode = CommentToggleMode.Toggle;
                return true;
            case "comment":
                mode = CommentToggleMode.Comment;
                return true;
            case "uncomment":
                mode = CommentToggleMode.Uncomment;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    private static ApplyWorkspaceEditParams BuildWorkspaceEdit(
        DocumentUri uri,
        GherkinCommentToggleResult result,
        string[] lines)
    {
        var textEdits = new TextEditContainer(
            result.Edits.Select(e => new TextEdit
            {
                // End character = line length so the range covers the full line content
                // (not the newline), turning this into a replacement rather than an insertion.
                Range   = new LspRange(
                    new Position(e.StartLine, 0),
                    new Position(e.EndLine, e.EndLine < lines.Length ? lines[e.EndLine].Length : 0)),
                NewText = e.NewText
            }));

        return new ApplyWorkspaceEditParams
        {
            Edit = new WorkspaceEdit
            {
                DocumentChanges = new Container<WorkspaceEditDocumentChange>(
                    new WorkspaceEditDocumentChange(new TextDocumentEdit
                    {
                        TextDocument = new OptionalVersionedTextDocumentIdentifier
                        {
                            Uri     = uri,
                            Version = null
                        },
                        Edits = textEdits
                    }))
            }
        };
    }
}
