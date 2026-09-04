using System.Text.RegularExpressions;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Rename;
using Reqnroll.IdeSupport.LSP.Server.Discovery.Roslyn;
using Reqnroll.IdeSupport.LSP.Server.Features.Rename;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.Rename;

public class StepRenameHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();
    private readonly IDocumentBufferService         _documentBuffer = Substitute.For<IDocumentBufferService>();
    private readonly ICSharpFileTextCache          _csharpFileTextCache = new CSharpFileTextCache();
    private readonly ICSharpBindingDiscoveryService _csharpDiscoveryService = Substitute.For<ICSharpBindingDiscoveryService>();
    private readonly ILanguageServerFacade         _languageServer = Substitute.For<ILanguageServerFacade>();
    private readonly ClientIdeContext              _clientIdeContext = new(ide: null);
    private readonly IFileSystemForIDE             _fileSystem = new FileSystemForIDE();

    private static readonly DocumentUri CsUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
    private static string CsPath => CsUri.GetFileSystemPath();

    public StepRenameHandlerTests()
    {
        // No project owners → FindUsages is called with a null filter and there are
        // no feature-side edits; the tests focus on the generated C# attribute edit.
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
                     .Returns(Array.Empty<LspReqnrollProject>());
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(System.Array.Empty<StepBindingMatch>());

        // The client defaults to non-VS (ClientIdeContext with ide: null), so the
        // workspace/applyEdit push (VS-only, see #82) is not exercised unless a test
        // opts in via CreateSutForVisualStudio.
        var fakeReturns = Substitute.For<IResponseRouterReturns>();
        fakeReturns.Returning<ApplyWorkspaceEditResponse>(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ApplyWorkspaceEditResponse { Applied = true }));
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(fakeReturns);
    }

    private RenameHandler CreateSut() =>
        new(_matchService, _scopeManager, _registryLookup, _logger, _documentBuffer,
            _csharpFileTextCache, _csharpDiscoveryService, _languageServer, _clientIdeContext, _fileSystem);

    private RenameHandler CreateSutForVisualStudio() =>
        new(_matchService, _scopeManager, _registryLookup, _logger, _documentBuffer,
            _csharpFileTextCache, _csharpDiscoveryService, _languageServer, new ClientIdeContext("visualstudio"), _fileSystem);

    private RenameHandler CreateSutWithTelemetry(ILspTelemetryService telemetry) =>
        new(_matchService, _scopeManager, _registryLookup, _logger, _documentBuffer,
            _csharpFileTextCache, _csharpDiscoveryService, _languageServer, _clientIdeContext, _fileSystem, telemetry);

    // reqnroll/renameTargets (issue #139) is handled by the separate RenameTargetsHandler class;
    // it shares no session state with RenameHandler, so a freshly-composed instance over the
    // same substitute fields is equivalent to whatever DI would wire up.
    private RenameTargetsHandler CreateTargetsSut() =>
        new(_registryLookup,
            new RenameBindingResolver(_matchService, _scopeManager, new RenameSessionManager(), _logger),
            new CSharpAttributeLiteralResolver(_csharpFileTextCache, _documentBuffer, _logger, _fileSystem));

    private void SetupBuffer(string csText)
    {
        _documentBuffer
            .TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>())
            .Returns(ci =>
            {
                ci[1] = new DocumentBuffer(CsUri, 1, csText);
                return true;
            });
    }

    private void SetupBuffers(params (DocumentUri Uri, string Text)[] buffers)
    {
        var map = buffers.ToDictionary(b => b.Uri.ToString(), b => b.Text);
        _documentBuffer
            .TryGet(Arg.Any<DocumentUri>(), out Arg.Any<DocumentBuffer?>())
            .Returns(ci =>
            {
                var u = (DocumentUri)ci[0];
                if (map.TryGetValue(u.ToString(), out var text))
                {
                    ci[1] = new DocumentBuffer(u, 1, text);
                    return true;
                }
                ci[1] = null;
                return false;
            });
    }

    // ── Change-annotation negotiation (issue #70) ───────────────────────────────
    // By default _languageServer.ClientSettings is unstubbed (null), which HandleRenameAsync
    // treats as "no annotation support" — the existing tests above exercise that fallback
    // path implicitly. Call this to opt a test into the annotated DocumentChanges shape.
    private void SetChangeAnnotationSupport(bool documentChanges, bool changeAnnotationSupport)
    {
        _languageServer.ClientSettings.Returns(new InitializeParams
        {
            Capabilities = new ClientCapabilities
            {
                Workspace = new WorkspaceClientCapabilities
                {
                    WorkspaceEdit = new Supports<WorkspaceEditCapability?>(true, new WorkspaceEditCapability
                    {
                        DocumentChanges = documentChanges,
                        ChangeAnnotationSupport = changeAnnotationSupport
                            ? new WorkspaceEditSupportCapabilitiesChangeAnnotationSupport()
                            : null
                    })
                }
            }
        });
    }

    private static ProjectStepDefinitionBinding MakeBinding(
        ScenarioBlock type,
        Regex         regex,
        string?       specifiedExpression,
        int           line,
        int           column = 9,
        ProjectBindingImplementation? sharedImpl = null,
        string        method = "Steps.M()",
        int?          attributeSourceLine = null)
    {
        var impl = sharedImpl ?? new ProjectBindingImplementation(
            method, null, new SourceLocation(CsPath, line, column));
        return new ProjectStepDefinitionBinding(type, regex, null, impl, specifiedExpression,
            attributeSourceLine: attributeSourceLine);
    }

    private static RenameParams RenameAt(int line, int character, string newName) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = CsUri },
            Position     = new Position(line, character),
            NewName      = newName
        };

    // ── The F16 defect: registry expression is a discovery projection (regex), not the
    //    source literal (Cucumber expression). The attribute must still be located and
    //    edited even though `binding.Expression` does not equal the source string. ──────

    [Fact]
    public async Task Rename_edits_source_literal_even_when_registry_expression_is_a_regex_projection()
    {
        // Source uses a Cucumber expression …
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +          // 0-based line 6
            "        public void GivenTheFirstNumberIs(int number) { }\n" + // 0-based line 7
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        // … but the registry carries the regex projection produced by discovery.
        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);                                  // 1-based method line
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        // Position-based resolution: request maps to the binding's (line 8, col 9).
        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the renamed number is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"the renamed number is {int}\"");
        edits[0].Range.Start.Line.Should().Be(6, "the attribute literal lives on 0-based line 6");
    }

    // ── Regression (issue #170's sibling bug, found while fixing the rename-targets picker):
    //    CSharpAttributeLiteralResolver used to pick the "nearest candidate method" purely by
    //    Math.Abs(line distance) to the registry's (possibly stale) recorded method line, with no
    //    way to distinguish two different same-type step methods declared close together. A
    //    binding's AttributeSourceLine — the AST-derived exact attribute line already used
    //    elsewhere (ProjectBindingRegistry.CoversQuery, RenameBindingResolver) — must now be
    //    matched exactly first, so the correct attribute is edited even when the recorded method
    //    line has drifted and would otherwise point the "nearest" search at the wrong method. ──

    [Fact]
    public async Task Rename_uses_exact_attribute_line_not_nearest_method_when_two_same_type_methods_are_close()
    {
        const string csText =
            "using Reqnroll;\n" +                                     // line 1
            "namespace N\n" +                                         // 2
            "{\n" +                                                   // 3
            "    [Binding]\n" +                                       // 4
            "    public class Steps\n" +                              // 5
            "    {\n" +                                               // 6
            "        [Given(\"the first number is {int}\")]\n" +      // 7 (0-based line 6)
            "        public void GivenTheFirstNumberIs(int p0) { }\n" + // 8
            "\n" +                                                    // 9
            "        [Given(\"the second num is {int}\")]\n" +        // 10 (0-based line 9)
            "        public void GivenTheSecondNumIs(int p0) { }\n" + // 11
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        // Recorded method line (9) is stale — closer to the FIRST method's real line (8) than to
        // the SECOND method's real line (11) — but AttributeSourceLine (10) still points at the
        // correct, current attribute for "the second num" binding.
        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the second num is (.*)$"),
            specifiedExpression: "the second num is {int}",
            line: 9, column: 9,
            attributeSourceLine: 10);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 9, character: 8, newName: "the renamed num is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"the renamed num is {int}\"");
        edits[0].Range.Start.Line.Should().Be(9,
            "the SECOND method's attribute literal, not the nearer-by-line first method's");
    }

    // ── Issue #82: the server self-refreshes the C# binding registry for the edited .cs file
    //    directly (no round-trip through a client didChange notification), and pushes a
    //    workspace/applyEdit request only when talking to VS — the client whose custom
    //    interception pipe swallows the textDocument/rename response before VS's built-in LSP
    //    client can apply it. Other clients (e.g. VS Code) apply the returned WorkspaceEdit
    //    natively and must not also receive the push, or the edit would be applied twice. ──────

    [Fact]
    public async Task Rename_self_refreshes_the_csharp_binding_registry_with_the_edited_content()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the renamed number is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        await _csharpDiscoveryService.Received(1).UpdateFromSourceAsync(
            CsUri,
            Arg.Is<string>(t => t.Contains("\"the renamed number is {int}\"")),
            isOpen: false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rename_from_visual_studio_pushes_workspace_applyEdit()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var result = await CreateSutForVisualStudio().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the renamed number is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        _languageServer.Received(1).SendRequest(
            "workspace/applyEdit", Arg.Any<ApplyWorkspaceEditParams>());
    }

    [Fact]
    public async Task Rename_returns_null_and_does_not_refresh_caches_when_VS_rejects_the_edit()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        // Override the default "Applied = true" fixture stub with a rejection.
        var rejectedReturns = Substitute.For<IResponseRouterReturns>();
        rejectedReturns.Returning<ApplyWorkspaceEditResponse>(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ApplyWorkspaceEditResponse { Applied = false, FailureReason = "locked" }));
        _languageServer.SendRequest(Arg.Any<string>(), Arg.Any<ApplyWorkspaceEditParams>())
            .Returns(rejectedReturns);

        var result = await CreateSutForVisualStudio().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the renamed number is {int}"),
            CancellationToken.None);

        result.Should().BeNull("VS reported the edit was not applied, so the rename did not actually happen");
        await _csharpDiscoveryService.DidNotReceive().UpdateFromSourceAsync(
            Arg.Any<DocumentUri>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        _matchService.DidNotReceive().InvalidateAllForDocument(Arg.Any<string>());
    }

    [Fact]
    public async Task Rename_from_non_visual_studio_client_does_not_push_workspace_applyEdit()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the renamed number is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        _languageServer.DidNotReceive().SendRequest(
            "workspace/applyEdit", Arg.Any<ApplyWorkspaceEditParams>());
    }

    // ── Multiple same-type attributes on one method are disambiguated by the resolved
    //    binding's expression, not by source position. ─────────────────────────────────

    [Fact]
    public async Task Rename_multi_attribute_method_edits_only_the_selected_attribute_literal()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"alpha {int}\")]\n" +    // 0-based line 6
            "        [Given(\"beta {int}\")]\n" +     // 0-based line 7
            "        public void M(int x) { }\n" +    // 0-based line 8 → 1-based 9
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var impl = new ProjectBindingImplementation("Steps.M()", null, new SourceLocation(CsPath, 9, 9));
        var alpha = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given, new Regex("^alpha (.*)$"), null, impl, "alpha {int}");
        var beta = new ProjectStepDefinitionBinding(
            ScenarioBlock.Given, new Regex("^beta (.*)$"), null, impl, "beta {int}");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { alpha, beta }));

        var sut = CreateSut();

        // Pick the second attribute (beta) on this method.
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = CsUri.ToString(), Version = 0, AttributeIndex = 1 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            RenameAt(line: 8, character: 8, newName: "gamma {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"gamma {int}\"");
        edits[0].Range.Start.Line.Should().Be(7, "the selected (beta) attribute literal is on 0-based line 7");
    }

    // ── Cucumber parameter types must survive the write-back, even when the dialog seeded
    //    (and the user edited) the regex projection of the expression. ────────────────────

    [Fact]
    public async Task Rename_retains_original_cucumber_parameter_type_when_new_name_uses_regex_form()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +          // 0-based line 6
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        // The user edited the regex-form seed (number → no), keeping the (.*) slot.
        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the first no is (.*)"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var edits = result!.Changes![CsUri].ToList();
        edits.Should().ContainSingle();
        edits[0].NewText.Should().Be("\"the first no is {int}\"",
            "the original Cucumber {int} parameter type is preserved, not the regex (.*) the user typed");
    }

    // ── Regression (issue #55): prepareRename for a .cs binding must return the string
    //    literal's INNER text range, excluding the surrounding quote characters. A whole-line
    //    (or whole-token, quotes-included) range used to seed the rename dialog with the quotes
    //    still visible; if the user left them untouched, `newName` arrived already quoted, and
    //    BuildCSharpEdit's unconditional `"` + text + `"` wrapping doubled them, producing a
    //    stray trailing quote in the attribute, e.g. [Given("foo bar"")]. ─────────────────────

    [Fact]
    public async Task PrepareRename_from_cs_returns_literal_inner_range_excluding_quotes()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +          // 0-based line 6
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is {int}",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.GetProjectForUri(CsUri).Returns(project);
        _scopeManager.GetIndexedFeatureFiles(project).Returns(new List<string> { "/workspace/test.feature" });

        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position     = new Position(7, 8)
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsRange.Should().BeTrue(
            "a .cs-triggered prepareRename returns a bare Range — the buffer text at that range " +
            "already is the abstract expression, so no separate Placeholder is needed");
        var range = result.Range!;
        range.Start.Line.Should().Be(6, "the attribute literal lives on 0-based line 6");
        range.End.Line.Should().Be(6);

        var line = csText.Replace("\r\n", "\n").Split('\n')[6];
        line[range.Start.Character - 1].Should().Be('"',
            "the range must start right after the opening quote, not include it");
        line[range.End.Character].Should().Be('"',
            "the range must end right before the closing quote, not include it");
        line.Substring(range.Start.Character, range.End.Character - range.Start.Character)
            .Should().Be("the first number is {int}");
    }

    // ── renameTargets surfaces the live source expression so the dialog seeds the
    //    Cucumber form rather than the regex projection. ───────────────────────────────────

    [Fact]
    public async Task RenameTargets_reports_live_source_expression_not_the_regex_projection()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the first number is {int}\")]\n" +
            "        public void GivenTheFirstNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";
        SetupBuffer(csText);

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is (.*)",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var response = await CreateTargetsSut().HandleRenameTargetsAsync(
            new RenameTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position     = new Position(7, 8)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        var target = response!.Targets.Should().ContainSingle().Subject;
        target.Expression.Should().Be("the first number is {int}");
        target.Label.Should().Be("Given the first number is {int}");
    }

    // ── End-to-end: a Scenario Outline placeholder usage must be preserved in the feature edit,
    //    not replaced by the binding's {int} placeholder. ─────────────────────────────────────

    [Fact]
    public async Task Rename_preserves_scenario_outline_placeholder_in_feature_edit()
    {
        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"the second number is {int}\")]\n" +     // 0-based line 6
            "        public void GivenTheSecondNumberIs(int number) { }\n" +
            "    }\n" +
            "}\n";

        // The feature step uses an Examples placeholder, which does not match the numeric regex.
        const string featureText =
            "Feature: F\n" +
            "Scenario Outline: x\n" +
            "\tGiven the second number is <secondNumber>\n";   // step text at line 2, chars 7..42
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/x.feature");

        SetupBuffers((CsUri, csText), (featureUri, featureText));

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the second number is (-?\\d+)$"),
            specifiedExpression: "the second number is {int}",
            line: 8, column: 9);
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        var match = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: 38, length: 35),
            MatchResult.NoMatch);
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { match });

        var result = await CreateSut().HandleRenameAsync(
            RenameAt(line: 7, character: 8, newName: "the second no is {int}"),
            CancellationToken.None);

        result.Should().NotBeNull();
        var featureEdits = result!.Changes![featureUri].ToList();
        featureEdits.Should().ContainSingle();
        featureEdits[0].NewText.Should().Be("the second no is <secondNumber>",
            "the outline placeholder is preserved; the binding's {int} token must not leak into the feature");
    }

    // ── Feature-file renaming tests ─────────────────────────────────────────────
    // These cover the three new code paths: HandleRenameTargetsFromFeatureAsync,
    // FindBindingsAtFeatureStep, and the .feature branch of HandleRenameAsync.

    private static FeatureBindingMatchSet MakeFeatureMatchSet(
        string featureUri, ProjectStepDefinitionBinding binding,
        string scenarioBlock, string stepText, int stepLine, int stepChar)
    {
        var text = $"Feature: F\nScenario: S\n\t{scenarioBlock} {stepText}\n";
        var snapshot = new LspTextSnapshot(featureUri, 1, text);
        var stepPrefix = $"\t{scenarioBlock} ";
        var startOffset = text.IndexOf(stepPrefix + stepText) + stepPrefix.Length;
        var range = GherkinRange.FromPoint(snapshot, startOffset: startOffset, length: stepText.Length);
        var match = new StepBindingMatch(
            featureUri,
            range,
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));

        return new FeatureBindingMatchSet(
            featureUri,
            new ProjectOwner("/workspace/MyProject.csproj", ".NETCoreApp,Version=v8.0"),
            1, 1,
            new[] { match });
    }

    // Builds a match set where the step is genuinely ambiguous (2+ matching bindings), mirroring
    // how ProjectBindingRegistry.cs:123 turns multiple Defined items into Ambiguous ones via
    // CloneToAmbiguousItem() — MatchResultItem.CreateMatch alone always produces Type.Defined.
    private static FeatureBindingMatchSet MakeAmbiguousFeatureMatchSet(
        string featureUri, ProjectStepDefinitionBinding[] bindings,
        string scenarioBlock, string stepText, int stepLine, int stepChar)
    {
        var text = $"Feature: F\nScenario: S\n\t{scenarioBlock} {stepText}\n";
        var snapshot = new LspTextSnapshot(featureUri, 1, text);
        var stepPrefix = $"\t{scenarioBlock} ";
        var startOffset = text.IndexOf(stepPrefix + stepText) + stepPrefix.Length;
        var range = GherkinRange.FromPoint(snapshot, startOffset: startOffset, length: stepText.Length);
        var items = bindings
            .Select(b => MatchResultItem.CreateMatch(b, ParameterMatch.NotMatch).CloneToAmbiguousItem())
            .ToArray();
        var match = new StepBindingMatch(featureUri, range, MatchResult.CreateMultiMatch(items));

        return new FeatureBindingMatchSet(
            featureUri,
            new ProjectOwner("/workspace/MyProject.csproj", ".NETCoreApp,Version=v8.0"),
            1, 1,
            new[] { match });
    }

    private static LspReqnrollProject MakeTestProject() =>
        new(
            new ReqnrollProjectLoadedParams
            {
                ProjectFile = "/workspace/MyProject.csproj",
                ProjectFolder = "/workspace",
                TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0"
            },
            Substitute.For<Reqnroll.IdeSupport.Common.IIdeScope>());

    // ── Regression: prepareRename for a .feature step must return the step-text-only range
    //    (excluding the keyword and leading indentation), matching the range HandleRenameAsync
    //    later applies the edit at (usage.Range). A whole-line range used to seed the rename
    //    dialog with "\tThen the result should be 120"; submitting an edited version of that back
    //    duplicated the keyword when the edit was applied only at the step-text span, producing
    //    "\tThen \tThen the result should be 120" in the feature file. ──────────────────────────

    [Fact]
    public async Task PrepareRename_from_feature_returns_step_text_range_excluding_keyword_and_indentation()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10)
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.IsPlaceholderRange.Should().BeTrue(
            "a .feature-triggered prepareRename must seed the client with the abstract expression " +
            "as Placeholder, not the concrete step text, so newName always comes back unambiguous");
        result.PlaceholderRange!.Placeholder.Should().Be("to be or not to be",
            "the placeholder is the binding's abstract expression, not whatever is literally in the buffer");

        // MakeFeatureMatchSet builds the line as "\tThen to be or not to be" — the step text
        // starts right after the tab + "Then " (6 chars), not at column 0.
        var range = result.PlaceholderRange.Range!;
        range.Start.Line.Should().Be(2);
        range.Start.Character.Should().Be(6,
            "the range must start at the step text, excluding the keyword and indentation");
        range.End.Character.Should().NotBe(200,
            "a synthetic whole-line range was the bug this regression guards against");
    }

    // ── Regression/repro (issue #456): F2 rename on a parameterized step pre-highlights the
    //    wrong span in the rename box. Root cause is exactly what HandlePrepareRenameAsync's own
    //    remarks (above) describe: VS Code's built-in rename UI computes the user's pre-selection
    //    offset against the CONCRETE step text, then reapplies that same raw character offset into
    //    Placeholder — a different string whenever a parameter's rendered width differs from its
    //    abstract token (here "1" is 1 char, "{int}" is 5). That reapplication happens inside VS
    //    Code itself, not in this server, so it can't be driven directly from a server-side test —
    //    this test instead proves the drift using the server's own real Range/Placeholder output
    //    for the issue's exact reported scenario. ─────────────────────────────────────────────────

    [Fact]
    public async Task PrepareRename_Range_and_Placeholder_reproduce_issue_456s_selection_drift()
    {
        const string stepText   = "the client added 1 units of \"Electric guitar\" to the basket";
        const string expression = "the client added {int} units of {string} to the basket";

        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var binding = MakeBinding(
            ScenarioBlock.When,
            new Regex("^the client added (\\d+) units of \"(.*)\" to the basket$"),
            specifiedExpression: expression,
            line: 8, column: 9,
            method: "Steps.WhenTheClientAddedUnitsOf()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "And", stepText, stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        // Cursor/selection on "units" in the concrete text.
        var unitsOffset = stepText.IndexOf("units", StringComparison.Ordinal);
        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 5 + unitsOffset) // "\tAnd " prefix is 5 chars
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.PlaceholderRange!.Placeholder.Should().Be(expression,
            "the rename box is seeded with the abstract expression, not the concrete step text");

        // ── Reproduce the client-side drift using the server's real Range/Placeholder ──────────
        // A client that pre-selected "units" computes that selection's offset relative to
        // Range.Start in the CONCRETE text, then reapplies the same raw offset/length into
        // Placeholder verbatim (this is VS Code's built-in behavior, not our code).
        var selectionOffset = unitsOffset;
        var selectionLength = "units".Length;
        var placeholder = result.PlaceholderRange.Placeholder;
        var reapplied = placeholder.Substring(
            selectionOffset, Math.Min(selectionLength, placeholder.Length - selectionOffset));

        reapplied.Should().NotBe("units",
            $"reproduces issue #456: reapplying the concrete-text offset ({selectionOffset}) into " +
            $"Placeholder verbatim no longer lands on \"units\" once the {{int}} parameter (5 chars) " +
            $"has replaced the shorter concrete value \"1\" (1 char) earlier in the string -- got " +
            $"\"{reapplied}\" instead");
    }

    // ── Regression (issue #344 follow-up): confirmed live in VS — a .feature step bound to a
    //    method-name-style binding (a bare [Given], no explicit expression) has no attribute
    //    literal to rename at all. Before this fix, CSharpAttributeLiteralResolver's "nearest
    //    candidate method" fallback (finding no literal for THIS binding) silently snapped to a
    //    geometrically nearby, unrelated method that did have a literal — the rename box then
    //    showed that unrelated method's expression as the placeholder. prepareRename must now
    //    refuse the rename entirely (return null) rather than seed the dialog with a wrong or
    //    empty placeholder. ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PrepareRename_from_feature_refuses_when_the_matched_binding_is_method_name_style()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex(@"^(?i)The(?:[^\w\p{Sc}]*)First(?:[^\w\p{Sc}]*)Number(?:[^\w\p{Sc}]*)Is(?:[^\w\p{Sc}]*)(?<p0>.*?)(?:[^\w\p{Sc}]*)$"),
            specifiedExpression: null,
            line: 8, column: 9,
            method: "Steps.The_First_Number_Is_P0()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Given", "the first number is 50", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        // No .cs file text is ever set up (no SetupBuffer call) — irrelevant, since a
        // method-name-style binding must be refused before any file is even parsed.
        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10)
            },
            CancellationToken.None);

        result.Should().BeNull(
            "a method-name-style binding has no attribute literal to rename, so the dialog must not open at all");
    }

    // ── Regression (#82 follow-up): confirmed live in VS — after a successful rename, invoking
    //    F2 again on the same step showed the OLD (pre-rename) text in the "Rename to:" box,
    //    even though the editor and the .cs file both visibly showed the new text. Root cause:
    //    IDocumentBufferService deliberately never tracks .cs files (only .feature — see
    //    TextDocumentSyncHandler), so FindAttributeLiteralAsync's buffer lookup was always a
    //    no-op for .cs paths and it fell back to disk — which a rename applied via
    //    workspace/applyEdit never touches (edits only reach the buffer, not disk, until saved).
    //    A second rename attempt before saving therefore always read the pre-rename text back
    //    off disk. RenameHandler now remembers its own last-computed .cs edit and prefers it
    //    over disk for exactly this window. ──────────────────────────────────────────────────

    [Fact]
    public async Task PrepareRename_after_a_rename_reflects_the_new_text_not_the_stale_disk_copy()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";
        // Registered once, up front — matching production, where .cs files are never re-synced
        // into the buffer after a rename, so this stays the only (stale) copy available anywhere
        // outside the handler's own memory of its edit.
        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);

        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";
        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();

        // First rename: succeeds, edits the .cs attribute to "to be and not to be". The buffer
        // set up above is never updated by this — it stays frozen at the pre-rename text, same
        // as production where a rename applied via workspace/applyEdit is never re-synced back
        // into a buffer we track (.cs isn't tracked at all) and is never saved to disk.
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);
        var renameResult = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);
        renameResult.Should().NotBeNull();

        // Second attempt, same step, before any save or reopen — this is what F2 does.
        var prepareResult = await sut.HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10)
            },
            CancellationToken.None);

        prepareResult.Should().NotBeNull();
        prepareResult!.PlaceholderRange!.Placeholder.Should().Be("to be and not to be",
            "the second prepareRename must reflect the first rename's edit, not the stale " +
            "pre-rename text still sitting in the (untouched) buffer/disk copy");
    }

    // ── Regression (#82 follow-up): FindBindingsAtFeatureStep used to re-derive its own
    //    start/end-character bounds check from step.Range.StartLinePosition/EndLinePosition —
    //    the exact narrow, exact-text-span-only check #101 fixed on StepBindingMatch.Contains,
    //    just reimplemented here without picking up that fix. A cursor on the step's keyword or
    //    leading indentation (as opposed to the concrete step text itself) would silently return
    //    zero bindings, so F2 rename showed no prompt at all — confirmed from a manual VS
    //    session's server logs where 4 of 5 F2 attempts on the same step line failed this way. ──

    [Fact]
    public async Task PrepareRename_from_feature_resolves_when_cursor_is_on_the_keyword_not_the_step_text()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        // Line is "\tThen to be or not to be" — the step text span starts at character 6.
        // Character 2 sits on "Then" itself, well before that span.
        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 2)
            },
            CancellationToken.None);

        result.Should().NotBeNull("a cursor on the step's keyword should still resolve the binding");
        result!.PlaceholderRange!.Placeholder.Should().Be("to be or not to be");
    }

    // ── Regression (issue #47): prepareRename at a position with no renameable binding must
    //    return null quietly rather than throwing. The textDocument/prepareRename JSON-RPC
    //    handler in LanguageServerOptionsExtensions passes this return value straight through
    //    (LspRange?), so vscode-languageclient can treat it as "rename not supported here" and
    //    stay silent instead of surfacing a raw exception popup. ─────────────────────────────

    [Fact]
    public async Task PrepareRename_from_feature_returns_null_when_no_binding_matches_the_step()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>()));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);
        _scopeManager.GetIndexedFeatureFiles(project).Returns(new List<string> { featureUri.GetFileSystemPath()! });

        // No match set registered for this key → FindBindingsAtFeatureStep finds nothing.
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(false);

        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10)
            },
            CancellationToken.None);

        result.Should().BeNull("rename is not available at this position and must be a silent no-op, not an exception");
    }

    [Fact]
    public async Task RenameTargets_from_feature_returns_matched_binding()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var response = await CreateTargetsSut().HandleRenameTargetsAsync(
            new RenameTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        var target = response!.Targets.Should().ContainSingle().Subject;
        target.Expression.Should().Be("to be or not to be");
        target.Label.Should().Be("Then to be or not to be — Steps.ThenToBeOrNotToBe()");
    }

    [Fact]
    public async Task RenameTargets_from_feature_no_match_returns_empty_response()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        _scopeManager.ResolveOwners(featureUri).Returns(Array.Empty<LspReqnrollProject>());

        var response = await CreateTargetsSut().HandleRenameTargetsAsync(
            new RenameTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Targets.Should().BeEmpty();
    }

    // ── Regression: an ambiguous feature step (2+ matching bindings) must still surface
    //    every candidate through the rename-targets picker, and prepareRename must not report
    //    "no defined binding" for it. MatchResult.HasDefined is false for ambiguous steps (their
    //    items are typed Ambiguous, not Defined) — FindBindingsAtFeatureStep used to gate on
    //    HasDefined alone, silently excluding exactly the steps this feature exists to handle. ──

    [Fact]
    public async Task RenameTargets_from_feature_returns_all_targets_for_ambiguous_step()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var bindingInt = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^the result should be (-?\\d+)$"),
            specifiedExpression: "the result should be {int}",
            line: 8, column: 9,
            method: "Steps.ThenResultInt(Int32)");
        var bindingAny = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^the result should be (.*)$"),
            specifiedExpression: "the result should be (.*)",
            line: 10, column: 9,
            method: "Steps.ThenResultAny(String)");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { bindingInt, bindingAny }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeAmbiguousFeatureMatchSet(
            featureUri.ToString(), new[] { bindingInt, bindingAny },
            "Then", "the result should be 120", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var response = await CreateTargetsSut().HandleRenameTargetsAsync(
            new RenameTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Targets.Should().HaveCount(2,
            "both ambiguous bindings must be offered, not silently dropped because neither is individually 'Defined'");
        response.Targets.Select(t => t.Expression).Should()
            .BeEquivalentTo(new[] { "the result should be {int}", "the result should be (.*)" });
        response.Targets.Select(t => t.Label).Should().OnlyHaveUniqueItems(
            "identical-looking picker entries leave the user unable to tell which binding they're choosing — " +
            "the implementing method must be part of the label");
        response.Targets.Should().Contain(t => t.Label.EndsWith("Steps.ThenResultInt(Int32)"));
        response.Targets.Should().Contain(t => t.Label.EndsWith("Steps.ThenResultAny(String)"));
    }

    [Fact]
    public async Task PrepareRename_from_feature_succeeds_for_ambiguous_step()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var bindingInt = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^the result should be (-?\\d+)$"),
            specifiedExpression: "the result should be {int}",
            line: 8, column: 9,
            method: "Steps.ThenResultInt(Int32)");
        var bindingAny = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^the result should be (.*)$"),
            specifiedExpression: "the result should be (.*)",
            line: 10, column: 9,
            method: "Steps.ThenResultAny(String)");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { bindingInt, bindingAny }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });
        _scopeManager.GetProjectForUri(featureUri).Returns(project);

        var matchSet = MakeAmbiguousFeatureMatchSet(
            featureUri.ToString(), new[] { bindingInt, bindingAny },
            "Then", "the result should be 120", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var result = await CreateSut().HandlePrepareRenameAsync(
            new PrepareRenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10)
            },
            CancellationToken.None);

        result.Should().NotBeNull(
            "an ambiguous-but-defined step must still offer rename — this used to return null " +
            "and the server's null->throw wiring surfaced that as a visible client error popup");
    }

    [Fact]
    public async Task SelectRenameTarget_then_Rename_from_feature_edits_only_selected_ambiguous_binding()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"the result should be {int}\")]\n" +   // 0-based line 6
            "        public void ThenResultInt(int result) { }\n" + // 0-based line 7
            "        [Then(\"the result should be (.*)\")]\n" +     // 0-based line 8
            "        public void ThenResultAny(string result) { }\n" + // 0-based line 9
            "    }\n" +
            "}\n";
        SetupBuffers((csUri, csText));

        var bindingInt = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^the result should be (-?\\d+)$"),
            specifiedExpression: "the result should be {int}",
            line: 8, column: 9,
            method: "Steps.ThenResultInt(Int32)");
        var bindingAny = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^the result should be (.*)$"),
            specifiedExpression: "the result should be (.*)",
            line: 10, column: 9,
            method: "Steps.ThenResultAny(String)");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { bindingInt, bindingAny }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeAmbiguousFeatureMatchSet(
            featureUri.ToString(), new[] { bindingInt, bindingAny },
            "Then", "the result should be 120", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var sut = CreateSut();

        // Discover the target index for the "(.*)" binding rather than assuming ordering —
        // FindBindingsAtFeatureStep collects candidates via a HashSet, so index isn't contractual.
        // RenameTargetsHandler is a separate instance from `sut`, but both share the same
        // underlying _matchService/_scopeManager substitutes, so FindBindingsAtFeatureStep
        // resolves the identical candidate set/order for both.
        var targets = await CreateTargetsSut().HandleRenameTargetsAsync(
            new RenameTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);
        var anyTarget = targets!.Targets.Should()
            .ContainSingle(t => t.Expression == "the result should be (.*)").Subject;

        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = anyTarget.AttributeIndex },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "the total should be (.*)"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(csUri);
        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"the total should be (.*)\"",
            "only the selected (ThenResultAny) attribute should be edited");
        csEdit[0].Range.Start.Line.Should().Be(8,
            "the edit must land on the ThenResultAny attribute line, not ThenResultInt's");
    }

    // ── Regression: a namespace-qualified method name (as real discovery actually produces,
    //    e.g. "MyProj.StepDefinitions.CalculatorSteps.GivenX(Int32)") must not dominate the
    //    picker label — two ambiguous bindings in the same project share that namespace prefix,
    //    so keeping it pushes the only actually-distinguishing part (class + method) past the
    //    picker's visible width before the two labels diverge. ─────────────────────────────────

    [Fact]
    public async Task RenameTargets_from_feature_label_omits_namespace_from_method_qualifier()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var bindingInt = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (-?\\d+)$"),
            specifiedExpression: "the first number is {int}",
            line: 8, column: 9,
            method: "Minimal.StepDefinitions.CalculatorStepDefinitions.GivenTheFirstNumberIs(Int32)");
        var bindingAny = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^the first number is (.*)$"),
            specifiedExpression: "the first number is {int}",
            line: 10, column: 9,
            method: "Minimal.StepDefinitions.OtherStepDefinitions.GivenTheFirstNumberIs(Int32)");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { bindingInt, bindingAny }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeAmbiguousFeatureMatchSet(
            featureUri.ToString(), new[] { bindingInt, bindingAny },
            "Given", "the first number is 50", stepLine: 2, stepChar: 6);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var response = await CreateTargetsSut().HandleRenameTargetsAsync(
            new RenameTargetsParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14)
            },
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Targets.Should().HaveCount(2);
        response.Targets.Should().OnlyContain(t => !t.Label.Contains("Minimal.StepDefinitions"),
            "the shared namespace prefix must be dropped — it doesn't help distinguish ambiguous bindings and crowds out the part that does");
        response.Targets.Should().Contain(t => t.Label.EndsWith("CalculatorStepDefinitions.GivenTheFirstNumberIs(Int32)"));
        response.Targets.Should().Contain(t => t.Label.EndsWith("OtherStepDefinitions.GivenTheFirstNumberIs(Int32)"));
    }

    [Fact]
    public async Task Rename_from_feature_edits_both_feature_text_and_csharp_attribute()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);

        var featureEdit = result.Changes[featureUri].ToList();
        featureEdit.Should().ContainSingle();
        featureEdit[0].NewText.Should().Be("to be and not to be");

        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"to be and not to be\"");
    }

    // ── Change-annotation negotiation (issue #70), test conditions 1 & 5 (plan §5.2):
    //    a client that advertises documentChanges + changeAnnotationSupport gets a
    //    DocumentChanges-shaped, annotated WorkspaceEdit instead of the legacy Changes map. ──

    [Fact]
    public async Task Rename_returns_annotated_DocumentChanges_when_the_client_supports_change_annotations()
    {
        SetChangeAnnotationSupport(documentChanges: true, changeAnnotationSupport: true);

        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes.Should().BeNull();
        result.DocumentChanges.Should().NotBeNull();

        var docChanges = result.DocumentChanges!.ToList();
        docChanges.Should().HaveCount(2);

        var featureEdit = docChanges.Single(c => c.TextDocumentEdit!.TextDocument.Uri == featureUri).TextDocumentEdit!;
        featureEdit.TextDocument.Version.Should().BeNull(); // condition 5: versionless identifier
        var featureAnnotated = featureEdit.Edits.Single().Should().BeOfType<AnnotatedTextEdit>().Subject;
        featureAnnotated.NewText.Should().Be("to be and not to be");
        ((string)featureAnnotated.AnnotationId).Should().Be(RenameChangeAnnotations.Feature);

        var csEditEntry = docChanges.Single(c => c.TextDocumentEdit!.TextDocument.Uri == csUri).TextDocumentEdit!;
        var csAnnotated = csEditEntry.Edits.Single().Should().BeOfType<AnnotatedTextEdit>().Subject;
        csAnnotated.NewText.Should().Be("\"to be and not to be\"");
        ((string)csAnnotated.AnnotationId).Should().Be(RenameChangeAnnotations.Binding);

        result.ChangeAnnotations.Should().ContainKey(RenameChangeAnnotations.Feature);
        result.ChangeAnnotations.Should().ContainKey(RenameChangeAnnotations.Binding);
    }

    [Fact]
    public async Task Rename_falls_back_to_the_legacy_Changes_shape_when_the_client_lacks_changeAnnotationSupport()
    {
        // documentChanges alone (no changeAnnotationSupport) is exactly Visual Studio's
        // negotiated capability per Phase 0 — see docs/Rename-ChangeAnnotations-Implementation-Plan.md.
        SetChangeAnnotationSupport(documentChanges: true, changeAnnotationSupport: false);

        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);

        // Regression guard: structurally identical to the pre-#70 (mode-2) output.
        result.Should().NotBeNull();
        result!.DocumentChanges.Should().BeNull();
        result.ChangeAnnotations.Should().BeNull();
        result.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);
        result.Changes[featureUri].Should().ContainSingle(e => e.NewText == "to be and not to be");
        result.Changes[csUri].Should().ContainSingle(e => e.NewText == "\"to be and not to be\"");
    }

    // ── Regression (#82 follow-up): confirmed live in VS — after a successful rename, inlay
    //    hints silently disappeared for every step in the open feature file. HandleRenameAsync
    //    unconditionally invalidated the .feature match cache for every modified feature file,
    //    including ones still open in the editor. But applying the edit already triggers a real
    //    textDocument/didChange for open files, which reparses and correctly rebuilds the match
    //    cache through the normal sync pipeline — running our own invalidate afterward (it runs
    //    after awaiting the VS applyEdit round-trip, so it reliably loses that race) wipes out
    //    the freshly-rebuilt cache with nothing left to repopulate it, since the file's content
    //    isn't changing again. Invalidation should only apply to closed files, which never get a
    //    didChange and so would otherwise never get reparsed at all. ─────────────────────────────

    [Fact]
    public async Task Rename_does_not_invalidate_the_match_cache_for_an_open_feature_file()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";
        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";

        // Both files are open — this is what makes applying the edit trigger a real didChange
        // (and thus a correct reparse) rather than needing our own invalidation as a fallback.
        SetupBuffers((csUri, csText), (featureUri, featureText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        _matchService.DidNotReceive().InvalidateAllForDocument(featureUri.ToString());
    }

    [Fact]
    public async Task Rename_still_invalidates_the_match_cache_for_a_closed_feature_file()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Then(\"to be or not to be\")]\n" +
            "        public void ThenToBeOrNotToBe() { }\n" +
            "    }\n" +
            "}\n";

        // Only the .cs file is open; the .feature file is closed, so applying the edit will not
        // produce a didChange for it, and our own invalidation is the only way its stale match
        // cache entry ever gets cleared.
        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Then,
            new Regex("^to be or not to be$"),
            specifiedExpression: "to be or not to be",
            line: 8, column: 9,
            method: "Steps.ThenToBeOrNotToBe()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        const string featureText = "Feature: F\nScenario: S\n\tThen to be or not to be\n";
        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Then", "to be or not to be", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "to be or not to be";
        var stepOffset = featureText.IndexOf("\tThen " + stepText) + "\tThen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 14),
                NewName = "to be and not to be"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        _matchService.Received(1).InvalidateAllForDocument(featureUri.ToString());
    }

    [Fact]
    public async Task Rename_from_feature_without_session_resolves_binding_via_match_cache()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"I press add\")]\n" +
            "        public void GivenIPressAdd() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^I press add$"),
            specifiedExpression: "I press add",
            line: 8, column: 9,
            method: "Steps.GivenIPressAdd()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Given", "I press add", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string featureText = "Feature: F\nScenario: S\n\tGiven I press add\n";
        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "I press add";
        var stepOffset = featureText.IndexOf("\tGiven " + stepText) + "\tGiven ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10),
                NewName = "I choose add"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);
    }

    // ── Regression: VS Code seeds the .feature rename dialog with the step's concrete text
    //    (real parameter values), not the abstract expression, since prepareRename returns a
    //    whole-line range. A rename submitted from the feature file must therefore be
    //    reconciled back to an abstract expression before validation/propagation — otherwise
    //    the parameter-count check always fails and the rename silently no-ops. ──────────────

    [Fact]
    public async Task Rename_from_feature_with_concrete_parameter_value_updates_feature_and_csharp()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"I have {int} cukes\")]\n" +
            "        public void GivenIHaveCukes(int count) { }\n" +
            "    }\n" +
            "}\n";

        const string featureText = "Feature: F\nScenario: S\n\tGiven I have 5 cukes\n";
        SetupBuffers((csUri, csText), (featureUri, featureText));

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^I have (-?\\d+) cukes$"),
            specifiedExpression: "I have {int} cukes",
            line: 8, column: 9,
            method: "Steps.GivenIHaveCukes()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Given", "I have 5 cukes", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "I have 5 cukes";
        var stepOffset = featureText.IndexOf("\tGiven " + stepText) + "\tGiven ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();

        // Simulates F2 on "cukes": VS Code seeds the dialog with the whole concrete line and the
        // user edits only the static wording, keeping the parameter value (5) untouched.
        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 15),
                NewName = "I have 5 pickles"
            },
            CancellationToken.None);

        result.Should().NotBeNull("a parameterized step rename from the .feature file must not silently no-op");
        result!.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);

        var featureEdit = result.Changes[featureUri].ToList();
        featureEdit.Should().ContainSingle();
        featureEdit[0].NewText.Should().Be("I have 5 pickles",
            "the concrete parameter value must be preserved in the feature file");

        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"I have {int} pickles\"",
            "the {int} parameter type must be preserved in the binding attribute, not the concrete value 5");
    }

    [Fact]
    public async Task Rename_from_feature_with_already_abstract_new_name_is_used_as_is()
    {
        // VS's custom "Rename Step" command (RenameStepCommand.cs) seeds its own prompt with the
        // binding's abstract expression (placeholders intact) regardless of whether the cursor was
        // in the .cs or .feature file, then submits that abstract text verbatim as `newName`. This
        // must not be mistaken for VS Code's concrete-text submission and rejected/mangled by the
        // parameter-value reconciliation added for that case.
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [Given(\"I have {int} cukes\")]\n" +
            "        public void GivenIHaveCukes(int count) { }\n" +
            "    }\n" +
            "}\n";

        const string featureText = "Feature: F\nScenario: S\n\tGiven I have 5 cukes\n";
        SetupBuffers((csUri, csText), (featureUri, featureText));

        var binding = MakeBinding(
            ScenarioBlock.Given,
            new Regex("^I have (-?\\d+) cukes$"),
            specifiedExpression: "I have {int} cukes",
            line: 8, column: 9,
            method: "Steps.GivenIHaveCukes()");
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "Given", "I have 5 cukes", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        var snapshot = new LspTextSnapshot(featureUri.ToString(), 1, featureText);
        const string stepText = "I have 5 cukes";
        var stepOffset = featureText.IndexOf("\tGiven " + stepText) + "\tGiven ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(snapshot, startOffset: stepOffset, length: stepText.Length),
            MatchResult.CreateMultiMatch(new[]
            {
                MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch)
            }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();

        // Simulates VS's custom command: the prompt was seeded with, and the user edited, the
        // abstract expression "I have {int} cukes" directly — not the concrete feature line.
        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 15),
                NewName = "I have {int} pickles"
            },
            CancellationToken.None);

        result.Should().NotBeNull("an already-abstract newName must not be rejected as an unreconcilable parameter-value change");
        result!.Changes!.Should().ContainKey(featureUri);
        result.Changes.Should().ContainKey(csUri);

        var featureEdit = result.Changes[featureUri].ToList();
        featureEdit.Should().ContainSingle();
        featureEdit[0].NewText.Should().Be("I have 5 pickles",
            "the concrete parameter value must still be preserved in the feature file");

        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"I have {int} pickles\"");
    }

    [Fact]
    public async Task FindAttributeLiteralAsync_redirects_from_feature_to_csharp_source()
    {
        var featureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");
        var csUri = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
        var csPath = csUri.GetFileSystemPath();

        const string csText =
            "using Reqnroll;\n" +
            "namespace N\n" +
            "{\n" +
            "    [Binding]\n" +
            "    public class Steps\n" +
            "    {\n" +
            "        [When(\"something happens\")]\n" +
            "        public void WhenSomethingHappens() { }\n" +
            "    }\n" +
            "}\n";

        SetupBuffers((csUri, csText));

        var binding = MakeBinding(
            ScenarioBlock.When,
            new Regex("^something happens$"),
            specifiedExpression: "something happens",
            line: 8, column: 9,
            method: "Steps.WhenSomethingHappens()");

        binding = new ProjectStepDefinitionBinding(
            binding.StepDefinitionType,
            binding.Regex,
            binding.Scope,
            new ProjectBindingImplementation(
                "Steps.WhenSomethingHappens()",
                null,
                new SourceLocation(csPath, 8, 9)),
            binding.Expression);

        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.FromBindings(new[] { binding }));

        var project = MakeTestProject();
        _scopeManager.ResolveOwners(featureUri).Returns(new[] { project });

        var matchSet = MakeFeatureMatchSet(
            featureUri.ToString(), binding,
            "When", "something happens", stepLine: 2, stepChar: 5);
        _matchService.TryGet(Arg.Any<MatchSetKey>(), out Arg.Any<FeatureBindingMatchSet>())
            .Returns(ci =>
            {
                ci[1] = matchSet;
                return true;
            });

        const string whenFeatureText = "Feature: F\nScenario: S\n\tWhen something happens\n";
        const string whenStepText = "something happens";
        var whenStepOffset = whenFeatureText.IndexOf("\tWhen " + whenStepText) + "\tWhen ".Length;
        var usageMatch = new StepBindingMatch(
            featureUri.ToString(),
            GherkinRange.FromPoint(
                new LspTextSnapshot(featureUri.ToString(), 1, whenFeatureText),
                startOffset: whenStepOffset, length: whenStepText.Length),
            MatchResult.CreateMultiMatch(new[] { MatchResultItem.CreateMatch(binding, ParameterMatch.NotMatch) }));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { usageMatch });

        var sut = CreateSut();
        await sut.HandleSelectRenameTargetAsync(
            new SelectRenameTargetParams { Uri = featureUri.ToString(), Version = 0, AttributeIndex = 0 },
            CancellationToken.None);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = featureUri },
                Position = new Position(2, 10),
                NewName = "something changed"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Changes!.Should().ContainKey(csUri);

        var csEdit = result.Changes[csUri].ToList();
        csEdit.Should().ContainSingle();
        csEdit[0].NewText.Should().Be("\"something changed\"");
        csEdit[0].Range.Start.Line.Should().Be(6, "the [When] attribute literal is on 0-based line 6");
    }

    // ── Telemetry ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleRenameAsync_emits_rename_telemetry_on_success()
    {
        // Arrange — set up a minimal rename that produces an edit
        const string csText = """
            using Reqnroll;
            namespace S;
            class C
            {
                [When("something")]
                void M() { }
            }
            """;
        var binding = new ProjectStepDefinitionBinding(
            ScenarioBlock.When,
            new Regex("^something$"),
            null,
            new ProjectBindingImplementation("C.M", null, new SourceLocation(CsPath, 5, 1)),
            "something");
        var registry = ProjectBindingRegistry.FromBindings(new[] { binding });
        _registryLookup.GetRegistryForUri(CsUri).Returns(registry);
        SetupBuffer(csText);

        var telemetry = Substitute.For<ILspTelemetryService>();
        var sut = CreateSutWithTelemetry(telemetry);

        var result = await sut.HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position = new Position(4, 0),
                NewName = "something changed"
            },
            CancellationToken.None);

        result.Should().NotBeNull();
        telemetry.Received(1).SendEvent(
            "Rename step command executed",
            Arg.Is<Dictionary<string, object?>>(d => false.Equals(d["Erroneous"])));
    }

    [Fact]
    public async Task HandleRenameAsync_emits_erroneous_telemetry_with_a_reason_for_an_empty_new_name()
    {
        var telemetry = Substitute.For<ILspTelemetryService>();

        var result = await CreateSutWithTelemetry(telemetry).HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position = new Position(4, 0),
                NewName = ""
            },
            CancellationToken.None);

        result.Should().BeNull();
        telemetry.Received(1).SendEvent(
            "Rename step command executed",
            Arg.Is<Dictionary<string, object?>>(d =>
                true.Equals(d["Erroneous"]) && "InvalidRequest".Equals(d["Reason"])));
    }

    [Fact]
    public async Task HandleRenameAsync_emits_erroneous_telemetry_with_a_reason_when_the_registry_is_invalid()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);
        var telemetry = Substitute.For<ILspTelemetryService>();

        var result = await CreateSutWithTelemetry(telemetry).HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position = new Position(4, 0),
                NewName = "something changed"
            },
            CancellationToken.None);

        result.Should().BeNull();
        telemetry.Received(1).SendEvent(
            "Rename step command executed",
            Arg.Is<Dictionary<string, object?>>(d =>
                true.Equals(d["Erroneous"]) && "RegistryInvalid".Equals(d["Reason"])));
    }

    [Fact]
    public async Task HandleRenameAsync_emits_erroneous_telemetry_with_a_reason_when_no_binding_resolves()
    {
        // A valid but empty registry: no binding at the cursor position.
        _registryLookup.GetRegistryForUri(CsUri)
            .Returns(ProjectBindingRegistry.FromBindings(Array.Empty<ProjectStepDefinitionBinding>()));
        var telemetry = Substitute.For<ILspTelemetryService>();

        var result = await CreateSutWithTelemetry(telemetry).HandleRenameAsync(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = CsUri },
                Position = new Position(4, 0),
                NewName = "something changed"
            },
            CancellationToken.None);

        result.Should().BeNull();
        telemetry.Received(1).SendEvent(
            "Rename step command executed",
            Arg.Is<Dictionary<string, object?>>(d =>
                true.Equals(d["Erroneous"]) && "BindingNotResolved".Equals(d["Reason"])));
    }
}
