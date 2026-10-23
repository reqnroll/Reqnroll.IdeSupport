using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Configuration.EditorConfig;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using OmniSharp.Extensions.JsonRpc;
using Reqnroll.IdeSupport.LSP.Server.Features.Formatting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Formatting;

public class FormattingHandlerTests
{
    private const string FeatureText = "Feature: F\nScenario: S\nGiven a step\n";

    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly IEditorConfigOptionsProvider _editorConfigOptionsProvider = Substitute.For<IEditorConfigOptionsProvider>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
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
        _editorConfigOptionsProvider.GetEditorConfigOptionsByPath(Arg.Any<string>())
            .Returns(NullEditorConfigOptions.Instance);
    }

    private FormattingHandler CreateSut() =>
        new(_bufferService, _editorConfigOptionsProvider, _scopeManager, _logger, _telemetryService);

    private static FormattingOptions Options() => new() { TabSize = 4, InsertSpaces = true };

    private static DocumentFormattingParams FormatRequest(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri }, Options = Options() };

    private void SetupBuffer(DocumentUri uri, string text) =>
        _bufferService.TryGet(uri, out Arg.Any<DocumentBuffer?>())
            .Returns(x =>
            {
                x[1] = new DocumentBuffer(uri, 1, text);
                return true;
            });

    [Fact]
    public async Task Handle_document_formatting_parses_with_the_owning_projects_feature_language()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rnr-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "reqnroll.json"),
                "{ \"language\": { \"feature\": \"fr\" } }");
            var project = new LspReqnrollProject(
                new ReqnrollProjectLoadedParams
                {
                    WorkspaceFolder = folder,
                    ProjectFile = Path.Combine(folder, "My.csproj"),
                    ProjectFolder = folder,
                    OutputAssemblyPath = Path.Combine(folder, "bin", "My.dll"),
                    TargetFrameworkMoniker = "net10.0"
                },
                new LspIdeScope(_logger));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(folder, "a.feature"));
            _scopeManager.ResolvePrimaryOwner(uri).Returns(project);
            SetupBuffer(uri, "Fonctionnalité: F\nScénario: S\n      Etant donné une précondition\n");

            var edits = await CreateSut().Handle(FormatRequest(uri), CancellationToken.None);

            edits!.Single().NewText.Should().Contain("Etant donné une précondition").And.NotContain("      Etant");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Handle_document_formatting_applies_the_owning_projects_format_settings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rnr-fmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "reqnroll.json"),
                "{ \"ide\": { \"editor\": { \"gherkinFormat\": { \"indentFeatureChildren\": true } } } }");
            var project = new LspReqnrollProject(
                new ReqnrollProjectLoadedParams
                {
                    WorkspaceFolder = folder,
                    ProjectFile = Path.Combine(folder, "My.csproj"),
                    ProjectFolder = folder,
                    OutputAssemblyPath = Path.Combine(folder, "bin", "My.dll"),
                    TargetFrameworkMoniker = "net10.0"
                },
                new LspIdeScope(_logger));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(folder, "a.feature"));
            _scopeManager.ResolvePrimaryOwner(uri).Returns(project);
            SetupBuffer(uri, "Feature: F\nScenario: S\nGiven a step\n");

            var edits = await CreateSut().Handle(FormatRequest(uri), CancellationToken.None);

            edits!.Single().NewText.Should().Contain("\n    Scenario: S");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Handle_document_formatting_rejects_a_document_unknown_to_the_server()
    {
        var unknown = DocumentUri.FromFileSystemPath("/workspace/unknown.feature");
        _bufferService.TryGet(unknown, out Arg.Any<DocumentBuffer?>()).Returns(false);

        var act = () => CreateSut().Handle(FormatRequest(unknown), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<RpcErrorException>();
        exception.Which.Code.Should().Be(EditRejected.Code);
        exception.Which.Message.Should().Contain("not open");

        _telemetryService.Received(1).SendEvent(
            "Command.AutoFormatDocument",
            Arg.Is<Dictionary<string, object?>>(p =>
                (bool)p["IsSelectionFormatting"]! == false
                && (bool)p["Erroneous"]! == true
                && (string)p["Reason"]! == "DocumentUnknown"
                && !p.ContainsKey("EditCount")
                && !p.ContainsKey("DocumentLineBucket")));
    }

    [Fact]
    public async Task Handle_document_formatting_rejects_a_document_with_no_parsable_feature()
    {
        var orphan = DocumentUri.FromFileSystemPath("/workspace/orphan.feature");
        SetupBuffer(orphan, "Scenario: Orphan\n    Given a step\n");

        var act = () => CreateSut().Handle(FormatRequest(orphan), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<RpcErrorException>();
        exception.Which.Code.Should().Be(EditRejected.Code);
        exception.Which.Message.Should().Contain("no Feature could be parsed");

        _telemetryService.Received(1).SendEvent(
            "Command.AutoFormatDocument",
            Arg.Is<Dictionary<string, object?>>(p =>
                (bool)p["IsSelectionFormatting"]! == false
                && (bool)p["Erroneous"]! == true
                && (string)p["Reason"]! == "NoFeatureParsed"
                && !p.ContainsKey("EditCount")
                && (string)p["DocumentLineBucket"]! == "2-10"));
    }

    [Fact]
    public async Task Handle_range_formatting_rejects_a_document_with_no_parsable_feature()
    {
        var orphan = DocumentUri.FromFileSystemPath("/workspace/orphan.feature");
        SetupBuffer(orphan, "Scenario: Orphan\n    Given a step\n");

        var act = () => CreateSut().Handle(new DocumentRangeFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = orphan },
            Options = Options(),
            Range = new LspRange(new Position(0, 0), new Position(1, 0))
        }, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<RpcErrorException>();
        exception.Which.Code.Should().Be(EditRejected.Code);

        _telemetryService.Received(1).SendEvent(
            "Command.AutoFormatDocument",
            Arg.Is<Dictionary<string, object?>>(p =>
                (bool)p["IsSelectionFormatting"]! == true
                && (bool)p["Erroneous"]! == true
                && (string)p["Reason"]! == "NoFeatureParsed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    public async Task Handle_document_formatting_of_an_empty_document_is_no_change_not_a_rejection(string text)
    {
        var blank = DocumentUri.FromFileSystemPath("/workspace/blank.feature");
        SetupBuffer(blank, text);

        var result = await CreateSut().Handle(FormatRequest(blank), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_on_type_formatting_of_an_unknown_document_stays_silent()
    {
        var unknown = DocumentUri.FromFileSystemPath("/workspace/unknown.feature");
        _bufferService.TryGet(unknown, out Arg.Any<DocumentBuffer?>()).Returns(false);

        var result = await CreateSut().Handle(new DocumentOnTypeFormattingParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = unknown },
            Options = Options(),
            Position = new Position(0, 0),
            Character = "|"
        }, CancellationToken.None);

        result.Should().BeEmpty();
    }

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
            "Command.AutoFormatDocument",
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
            "Command.AutoFormatDocument",
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
            "Command.AutoFormatDocument",
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
            "Command.AutoFormatDocument",
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
