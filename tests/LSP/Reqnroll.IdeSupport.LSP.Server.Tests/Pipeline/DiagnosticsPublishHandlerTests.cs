using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Diagnostics;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;



using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Pipeline;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Pipeline;

public class DiagnosticsPublishHandlerTests
{
    private readonly IDocumentBufferService    _bufferService  = Substitute.For<IDocumentBufferService>();
    private readonly IBindingMatchService      _matchService   = Substitute.For<IBindingMatchService>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ILspWorkspaceScopeManager _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IDiagnosticsAggregator    _aggregator     = Substitute.For<IDiagnosticsAggregator>();
    private readonly ILanguageServerFacade     _facade         = Substitute.For<ILanguageServerFacade>();
    private readonly IIdeSupportLogger            _logger         = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public DiagnosticsPublishHandlerTests()
    {
        // Default: no primary owner resolved — handler falls back to Unknown key.
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);

        // Default: registry not ready — simulates pre-Connector state. Tests that need a
        // real registry can override this return with a non-Invalid ProjectBindingRegistry.
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                      .Returns(ProjectBindingRegistry.Invalid);
    }

    private DiagnosticsPublishHandler CreateSut() =>
        new(_bufferService, _matchService, _registryLookup, _scopeManager, _aggregator, _facade, _logger);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an LspTextSnapshot-backed GherkinRange so the handler's ResolvePosition
    /// helper has real line data to work with.
    /// </summary>
    private static GherkinDiagnostic MakeDiagnostic(
        string text,
        int startOffset,
        int length,
        GherkinDiagnosticSeverity severity,
        string source,
        string message)
    {
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, text);
        var range = GherkinRange.FromPoint(snapshot, startOffset, length);
        return new GherkinDiagnostic(message, range, severity, source);
    }

    private void SetupBuffer(IReadOnlyCollection<DeveroomTag> tags)
    {
        var buffer = new DocumentBuffer(FeatureUri, 1, "Feature: X\n", tags);
        _bufferService.TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>())
                      .Returns(x => { x[1] = buffer; return true; });
    }

    private void SetupNoBuffer()
    {
        _bufferService.TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>())
                      .Returns(x => { x[1] = null; return false; });
    }

    private void SetupAggregator(params GherkinDiagnostic[] diagnostics)
    {
        _aggregator
            .Aggregate(Arg.Any<IReadOnlyCollection<DeveroomTag>>(), Arg.Any<FeatureBindingMatchSet>())
            .Returns(diagnostics);
    }

    // ── No-buffer guard ───────────────────────────────────────────────────────

    [Fact]
    public async Task Does_not_push_when_buffer_is_not_registered()
    {
        SetupNoBuffer();

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishDiagnosticsParams>());
    }

    [Fact]
    public async Task Does_not_push_when_buffer_has_null_tags()
    {
        var buffer = new DocumentBuffer(FeatureUri, 1, "Feature: X\n", null);
        _bufferService.TryGet(FeatureUri, out Arg.Any<DocumentBuffer?>())
                      .Returns(x => { x[1] = buffer; return true; });

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.DidNotReceive().SendNotification(Arg.Any<string>(), Arg.Any<PublishDiagnosticsParams>());
    }

    // ── Notification method and URI ───────────────────────────────────────────

    [Fact]
    public async Task Sends_textDocument_publishDiagnostics_notification()
    {
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator();

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.Received(1).SendNotification(
            "textDocument/publishDiagnostics",
            Arg.Any<PublishDiagnosticsParams>());
    }

    [Fact]
    public async Task Pushed_params_contain_the_correct_URI()
    {
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator();

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.Received(1).SendNotification(
            "textDocument/publishDiagnostics",
            Arg.Is<PublishDiagnosticsParams>(p => p.Uri == FeatureUri));
    }

    // ── Empty diagnostics ─────────────────────────────────────────────────────

    [Fact]
    public async Task Pushes_empty_diagnostics_when_aggregator_returns_none()
    {
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator();  // returns empty array

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        _facade.Received(1).SendNotification(
            "textDocument/publishDiagnostics",
            Arg.Is<PublishDiagnosticsParams>(p => !p.Diagnostics.Any()));
    }

    // ── Severity mapping ──────────────────────────────────────────────────────

    [Fact]
    public async Task Error_severity_maps_to_DiagnosticSeverity_Error()
    {
        const string featureText = "Feature: F\nScenario: S\n    Given step\n";
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator(MakeDiagnostic(featureText, 0, 7, GherkinDiagnosticSeverity.Error,
            DiagnosticsAggregator.ParserSource, "parse error"));

        PublishDiagnosticsParams? captured = null;
        _facade.When(f => f.SendNotification("textDocument/publishDiagnostics", Arg.Any<PublishDiagnosticsParams>()))
               .Do(ci => captured = ci.Arg<PublishDiagnosticsParams>());

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Diagnostics.Should().ContainSingle()
                 .Which.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Warning_severity_maps_to_DiagnosticSeverity_Warning()
    {
        const string featureText = "Feature: F\nScenario: S\n    Given step\n";
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator(MakeDiagnostic(featureText, 0, 7, GherkinDiagnosticSeverity.Warning,
            DiagnosticsAggregator.BindingSource, DiagnosticsAggregator.UndefinedStepMessage));

        PublishDiagnosticsParams? captured = null;
        _facade.When(f => f.SendNotification("textDocument/publishDiagnostics", Arg.Any<PublishDiagnosticsParams>()))
               .Do(ci => captured = ci.Arg<PublishDiagnosticsParams>());

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        captured!.Diagnostics.Should().ContainSingle()
                 .Which.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    // ── Source and message pass-through ──────────────────────────────────────

    [Fact]
    public async Task Source_and_message_are_forwarded_to_the_LSP_Diagnostic()
    {
        const string featureText = "Feature: F\n";
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator(MakeDiagnostic(featureText, 0, 7, GherkinDiagnosticSeverity.Warning,
            DiagnosticsAggregator.BindingSource, DiagnosticsAggregator.UndefinedStepMessage));

        PublishDiagnosticsParams? captured = null;
        _facade.When(f => f.SendNotification("textDocument/publishDiagnostics", Arg.Any<PublishDiagnosticsParams>()))
               .Do(ci => captured = ci.Arg<PublishDiagnosticsParams>());

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        var diag = captured!.Diagnostics.Single();
        diag.Source.Should().Be(DiagnosticsAggregator.BindingSource);
        diag.Message.Should().Be(DiagnosticsAggregator.UndefinedStepMessage);
    }

    [Fact]
    public async Task Error_with_BindingSource_forwards_source_and_message()
    {
        const string featureText = "Feature: F\n";
        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator(MakeDiagnostic(featureText, 0, 7, GherkinDiagnosticSeverity.Error,
            DiagnosticsAggregator.BindingSource, "Ambiguous step definition."));

        PublishDiagnosticsParams? captured = null;
        _facade.When(f => f.SendNotification("textDocument/publishDiagnostics", Arg.Any<PublishDiagnosticsParams>()))
               .Do(ci => captured = ci.Arg<PublishDiagnosticsParams>());

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        var diag = captured!.Diagnostics.Single();
        diag.Severity.Should().Be(DiagnosticSeverity.Error);
        diag.Source.Should().Be(DiagnosticsAggregator.BindingSource);
        diag.Message.Should().Be("Ambiguous step definition.");
    }

    // ── Range → LSP Position conversion ──────────────────────────────────────

    [Fact]
    public async Task Range_is_converted_to_correct_LSP_line_and_character_positions()
    {
        // "Feature: F\n"  → line 0, chars 0-10
        // "Scenario: S\n" → line 1, chars 0-11
        // "    Given step\n" → line 2, chars 0-15
        //                              "    Given " starts at col 0, step text starts at col 10
        const string featureText = "Feature: F\nScenario: S\n    Given step\n";

        // Target: "step" on line 2 starting at character 10 (after "    Given ")
        // Absolute offset of "step": 10 + 1 + 11 + 1 + 10 = 33
        // Let's compute: "Feature: F\n" = 11 chars, "Scenario: S\n" = 12 chars,
        //                "    Given " = 10 chars  → start offset = 11+12+10 = 33
        const int stepStart = 33;
        const int stepLen = 4;  // "step"

        SetupBuffer(Array.Empty<DeveroomTag>());
        SetupAggregator(MakeDiagnostic(featureText, stepStart, stepLen,
            GherkinDiagnosticSeverity.Warning, DiagnosticsAggregator.BindingSource, "msg"));

        PublishDiagnosticsParams? captured = null;
        _facade.When(f => f.SendNotification("textDocument/publishDiagnostics", Arg.Any<PublishDiagnosticsParams>()))
               .Do(ci => captured = ci.Arg<PublishDiagnosticsParams>());

        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        var lspRange = captured!.Diagnostics.Single().Range;
        lspRange.Start.Line.Should().Be(2);
        lspRange.Start.Character.Should().Be(10);
        lspRange.End.Line.Should().Be(2);
        lspRange.End.Character.Should().Be(14);  // 10 + 4
    }

    // ── Q24: registry-ready guard ────────────────────────────────────────────

    /// <summary>
    /// A real-but-empty match set that is clearly not <see cref="FeatureBindingMatchSet.Empty"/>.
    /// Used by the Q24 guard tests to verify the guard correctly substitutes or preserves the
    /// match set depending on registry state. Avoids the heavy scaffolding of real
    /// <see cref="StepBindingMatch"/> entries.
    /// </summary>
    private static readonly FeatureBindingMatchSet NonEmptyMatchSet =
        new("non-empty-doc", ProjectOwner.Unknown,
            documentVersion: 1, registryVersion: 0,
            Array.Empty<StepBindingMatch>());

    /// <summary>
    /// A <see cref="ProjectBindingRegistry"/> instance that is not the <c>Invalid</c> singleton
    /// — used to simulate the post-Connector ready state without needing binding scaffolding.
    /// </summary>
    private static readonly ProjectBindingRegistry ReadyRegistry =
        ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>());

    [Fact]
    public async Task Aggregator_receives_empty_match_set_when_registry_is_Invalid()
    {
        // Arrange
        SetupBuffer(Array.Empty<DeveroomTag>());
        // Registry stays Invalid (default from constructor).
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
                     .Returns(x =>
                     {
                         x[1] = NonEmptyMatchSet;
                         return true;
                     });
        SetupAggregator();

        // Act
        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        // Assert — the real match set must NOT have been forwarded; the Q24 guard
        // substitutes FeatureBindingMatchSet.Empty when the registry is Invalid.
        _aggregator.Received(1).Aggregate(
            Arg.Any<IReadOnlyCollection<DeveroomTag>>(),
            FeatureBindingMatchSet.Empty);
    }

    [Fact]
    public async Task Aggregator_receives_real_match_set_when_registry_is_ready()
    {
        // Arrange
        SetupBuffer(Array.Empty<DeveroomTag>());
        _registryLookup.GetRegistryForUri(FeatureUri).Returns(ReadyRegistry);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
                     .Returns(x =>
                     {
                         x[1] = NonEmptyMatchSet;
                         return true;
                     });
        SetupAggregator();

        // Act
        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        // Assert — the real match set IS forwarded because the registry is ready
        // (not the Invalid singleton).
        _aggregator.Received(1).Aggregate(
            Arg.Any<IReadOnlyCollection<DeveroomTag>>(),
            NonEmptyMatchSet);
    }

    [Fact]
    public async Task Parse_error_diagnostics_are_published_when_registry_is_Invalid()
    {
        // Arrange
        const string featureText = "Feature: F\n  Scenario: S\n    Given step\n";
        var snapshot = new LspTextSnapshot(FeatureUri.ToString(), 1, featureText);
        var parseErrorTag = new DeveroomTag(
            DeveroomTagTypes.ParserError,
            GherkinRange.FromPoint(snapshot, 0, 10),
            "syntax error");
        SetupBuffer(new[] { parseErrorTag });
        // Registry stays Invalid (default from constructor).
        SetupAggregator(MakeDiagnostic(featureText, 0, 10,
            GherkinDiagnosticSeverity.Error, DiagnosticsAggregator.ParserSource, "syntax error"));

        // Act
        await CreateSut().Handle(new MatchCacheChangedNotification(FeatureUri, 1), CancellationToken.None);

        // Assert — parse error diagnostics survive despite the Invalid registry.
        _facade.Received(1).SendNotification("textDocument/publishDiagnostics",
            Arg.Is<PublishDiagnosticsParams>(p => p.Diagnostics.Any()));
    }
}
