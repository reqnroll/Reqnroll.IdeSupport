using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Commenting;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.Commenting;


using Reqnroll.IdeSupport.LSP.Server.Features.TextSync;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Commenting;

public class CommentToggleHandlerTests
{
    private readonly IDocumentBufferService   _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly ICommentToggleService     _toggleService  = Substitute.For<ICommentToggleService>();
    private readonly ILanguageServerFacade     _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger           _logger         = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri =
        DocumentUri.FromFileSystemPath("/workspace/test.feature");

    private CommentToggleHandler CreateSut() =>
        new(_bufferService, _toggleService, _languageServer, _logger);

    private CommentToggleHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_bufferService, _toggleService, _languageServer, _logger, telemetry);

    private static ExecuteCommandParams MakeParams(string command, params object[] args)
        => new()
        {
            Command   = command,
            Arguments = new JArray(args)
        };

    private void SetupApplyEditRequest()
    {
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        fakeReturns.Returning<ApplyWorkspaceEditResponse>(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ApplyWorkspaceEditResponse { Applied = true }));
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(fakeReturns);
    }

    private void SetupBuffer(DocumentUri uri, string text)
    {
        var buf = new DocumentBuffer(uri, 1, text, Array.Empty<IdeSupportTag>());
        DocumentBuffer? outBuf;
        _bufferService.TryGet(uri, out outBuf)
            .Returns(x =>
            {
                x[1] = buf;
                return true;
            });
    }

    // ── Guard rails ───────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_command_does_nothing_Async()
    {
        await CreateSut().Handle(
            MakeParams("unknown.command"), CancellationToken.None);

        _languageServer.DidNotReceive().SendRequest(
            Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task Missing_arguments_does_nothing_Async()
    {
        await CreateSut().Handle(
            new ExecuteCommandParams { Command = "reqnroll.toggleComment" },
            CancellationToken.None);

        _languageServer.DidNotReceive().SendRequest(
            Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task Buffer_not_found_does_nothing_Async()
    {
        DocumentBuffer? ignored;
        _bufferService.TryGet(FeatureUri, out ignored).Returns(false);

        await CreateSut().Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 0, 0),
            CancellationToken.None);

        _languageServer.DidNotReceive().SendRequest(
            Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>());
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sends_applyEdit_request_Async()
    {
        SetupBuffer(FeatureUri, "Feature: F\n");
        SetupApplyEditRequest();
        _toggleService.ToggleComment(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new GherkinCommentToggleResult(new[]
            {
                new GherkinCommentEdit(0, 0, "# Feature: F")
            }));

        await CreateSut().Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 0, 0),
            CancellationToken.None);

        _languageServer.Received(1).SendRequest("workspace/applyEdit",
            Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task ApplyEdit_contains_correct_uri_Async()
    {
        SetupBuffer(FeatureUri, "Feature: F\n");
        SetupApplyEditRequest();
        _toggleService.ToggleComment("Feature: F\n", 0, 0)
            .Returns(new GherkinCommentToggleResult(new[]
            {
                new GherkinCommentEdit(0, 0, "# Feature: F")
            }));

        await CreateSut().Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 0, 0),
            CancellationToken.None);

        _languageServer.Received(1).SendRequest("workspace/applyEdit",
            Arg.Is<ApplyWorkspaceEditParams>(p =>
                p.Edit.DocumentChanges!.First().TextDocumentEdit!.TextDocument.Uri == FeatureUri));
    }

    [Fact]
    public async Task Toggle_called_with_correct_parameters_Async()
    {
        SetupBuffer(FeatureUri, "Given a step\nWhen step2\n");
        SetupApplyEditRequest();
        _toggleService.ToggleComment(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new GherkinCommentToggleResult(Array.Empty<GherkinCommentEdit>()));

        await CreateSut().Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 1, 2),
            CancellationToken.None);

        _toggleService.Received(1).ToggleComment("Given a step\nWhen step2\n", 1, 2);
    }

    [Fact]
    public async Task ApplyEdit_range_is_replacement_not_insertion_Async()
    {
        // "Feature: F" has length 10; the range end character must be 10, not 0.
        // A zero-length range (start == end at col 0) inserts text rather than
        // replacing it, duplicating the existing line content (regression guard).
        const string lineContent = "Feature: F";
        SetupBuffer(FeatureUri, lineContent + "\n");
        SetupApplyEditRequest();
        _toggleService.ToggleComment(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new GherkinCommentToggleResult(new[]
            {
                new GherkinCommentEdit(0, 0, "# " + lineContent)
            }));

        await CreateSut().Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 0, 0),
            CancellationToken.None);

        _languageServer.Received(1).SendRequest("workspace/applyEdit",
            Arg.Is<ApplyWorkspaceEditParams>(p =>
                p.Edit.DocumentChanges!
                    .First().TextDocumentEdit!.Edits
                    .First().Range.End.Character == lineContent.Length));
    }

    [Theory]
    [InlineData("Feature: F\n")]          // LF
    [InlineData("Feature: F\r\n")]        // CRLF
    public async Task ApplyEdit_range_end_character_excludes_line_ending_Async(string documentText)
    {
        // End character must equal the length of the line content, regardless of
        // whether the file uses LF or CRLF line endings.
        const int expectedEndChar = 10; // "Feature: F".Length
        SetupBuffer(FeatureUri, documentText);
        SetupApplyEditRequest();
        _toggleService.ToggleComment(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new GherkinCommentToggleResult(new[]
            {
                new GherkinCommentEdit(0, 0, "# Feature: F")
            }));

        await CreateSut().Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 0, 0),
            CancellationToken.None);

        _languageServer.Received(1).SendRequest("workspace/applyEdit",
            Arg.Is<ApplyWorkspaceEditParams>(p =>
                p.Edit.DocumentChanges!
                    .First().TextDocumentEdit!.Edits
                    .First().Range.End.Character == expectedEndChar));
    }

    // ── Telemetry ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_emits_command_telemetry()
    {
        SetupBuffer(FeatureUri, "Feature: F\nScenario: S\n    Given a step\n");
        SetupApplyEditRequest();
        _toggleService.ToggleComment(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new GherkinCommentToggleResult(new List<GherkinCommentEdit>()));

        var telemetry = Substitute.For<ILspTelemetryService>();
        await CreateSutWithTelemetry(telemetry).Handle(
            MakeParams("reqnroll.toggleComment", FeatureUri.ToString(), 0, 0),
            CancellationToken.None);

        telemetry.Received(1).SendEvent("CommentUncomment command executed", Arg.Any<Dictionary<string, object?>>());
    }

    [Fact]
    public async Task Handle_does_not_emit_telemetry_on_wrong_command()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();
        await CreateSutWithTelemetry(telemetry).Handle(
            MakeParams("reqnroll.unknownCommand"),
            CancellationToken.None);

        telemetry.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }
}
