using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Server.Features.Formatting;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Formatting;

public class FormattingHandlerTests
{
    private const string FeatureText = "Feature: F\nScenario: S\n    Given a step\n";

    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IEditorConfigOptionsProvider _editorConfigOptionsProvider = Substitute.For<IEditorConfigOptionsProvider>();
    private readonly IIdeSupportConfigurationProvider _configProvider = Substitute.For<IIdeSupportConfigurationProvider>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly ILspTelemetryService _telemetryService = Substitute.For<ILspTelemetryService>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public FormattingHandlerTests()
    {
        _bufferService.TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(FeatureUri, 1, FeatureText);
                return true;
            });
        _configProvider.GetConfiguration().Returns(new IdeSupportConfiguration());
        _editorConfigOptionsProvider.GetEditorConfigOptionsByPath(Arg.Any<string>())
            .Returns(Substitute.For<IEditorConfigOptions>());
    }

    private FormattingHandler CreateSut() =>
        new(_bufferService, _editorConfigOptionsProvider, _configProvider, _logger, _telemetryService);

    private static FormattingOptions Options() => new() { TabSize = 4, InsertSpaces = true };

    [Fact]
    public async Task Handle_document_formatting_sends_telemetry_with_IsSelectionFormatting_false()
    {
        var request = new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
            Options = Options()
        };

        await CreateSut().Handle(request, CancellationToken.None);

        _telemetryService.Received(1).SendEvent(
            "AutoFormatDocument command executed",
            Arg.Is<Dictionary<string, object?>>(p =>
                (bool)p["IsSelectionFormatting"]! == false
                && (int)p["EditCount"]! == 1
                && (string)p["DocumentLineBucket"]! == "2-10"));
    }

    [Fact]
    public async Task Handle_range_formatting_sends_telemetry_with_IsSelectionFormatting_true()
    {
        var request = new DocumentRangeFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
            Options = Options(),
            Range = new LspRange(new Position(0, 0), new Position(1, 0))
        };

        await CreateSut().Handle(request, CancellationToken.None);

        _telemetryService.Received(1).SendEvent(
            "AutoFormatDocument command executed",
            Arg.Is<Dictionary<string, object?>>(p =>
                (bool)p["IsSelectionFormatting"]! == true && p.ContainsKey("EditCount")));
    }

    [Fact]
    public async Task Handle_document_formatting_of_an_already_formatted_document_reports_no_edits()
    {
        var first = await CreateSut().Handle(new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
            Options = Options()
        }, CancellationToken.None);
        var formatted = first!.Single().NewText;
        var settled = DocumentUri.FromFileSystemPath("/workspace/settled.feature");
        _bufferService.TryGet(settled, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(settled, 1, formatted);
                return true;
            });
        _telemetryService.ClearReceivedCalls();

        await CreateSut().Handle(new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = settled },
            Options = Options()
        }, CancellationToken.None);

        _telemetryService.Received(1).SendEvent(
            "AutoFormatDocument command executed",
            Arg.Is<Dictionary<string, object?>>(p => (int)p["EditCount"]! == 0));
    }

    [Fact]
    public async Task Handle_document_formatting_of_a_misformatted_document_reports_one_edit()
    {
        var messy = DocumentUri.FromFileSystemPath("/workspace/messy.feature");
        _bufferService.TryGet(messy, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(messy, 1, "Feature: F\n      Scenario: S\nGiven a step\n");
                return true;
            });

        await CreateSut().Handle(new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = messy },
            Options = Options()
        }, CancellationToken.None);

        _telemetryService.Received(1).SendEvent(
            "AutoFormatDocument command executed",
            Arg.Is<Dictionary<string, object?>>(p => (int)p["EditCount"]! == 1));
    }

    [Fact]
    public async Task Handle_on_type_formatting_does_not_send_telemetry()
    {
        // On-type table formatting fires on every keystroke inside a table (|, tab, newline) —
        // a continuous editor feature, not a discrete user command, so it deliberately has no
        // usage event (see CodeActionHandler/FindStepUsagesHandler for the "commands
        // only" telemetry scoping decision this mirrors).
        var request = new DocumentOnTypeFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = FeatureUri },
            Options = Options(),
            Position = new Position(0, 0),
            Character = "|"
        };

        await CreateSut().Handle(request, CancellationToken.None);

        _telemetryService.DidNotReceiveWithAnyArgs().SendEvent(default!, default!);
    }
}
