using AwesomeAssertions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll;
using Reqnroll.IdeSupport.LSP.Server.Specs.Support;

namespace Reqnroll.IdeSupport.LSP.Server.Specs.StepDefinitions;

[Binding]
public sealed class ProtocolSteps
{
    private readonly LspScenarioContext _ctx;

    public ProtocolSteps(LspScenarioContext ctx) => _ctx = ctx;

    // ── Given ──────────────────────────────────────────────────────────────────

    [Given("the LSP server is started")]
    public async Task GivenTheLspServerIsStarted() => await _ctx.EnsureStartedAsync();

    [Given(@"the LSP server is started for IDE ""(.*)""")]
    public async Task GivenTheLspServerIsStartedForIde(string ide) => await _ctx.EnsureStartedAsync(ide);

    // Issue #70: the harness's simulated client negotiates LSP 3.16 change-annotation support
    // only when a scenario opts in via this step — every other scenario keeps the default
    // (unsupported) capabilities so its assertions against WorkspaceEdit.Changes stay unaffected.
    [Given("the LSP client supports rename change annotations")]
    public async Task GivenTheLspClientSupportsRenameChangeAnnotations() =>
        await _ctx.EnsureStartedAsync(supportsChangeAnnotations: true);

    // ── When ───────────────────────────────────────────────────────────────────

    [When(@"the feature file ""(.*)"" is opened with")]
    public async Task WhenTheFeatureFileIsOpenedWith(string fileName, string content)
    {
        await _ctx.EnsureStartedAsync();
        var uri = _ctx.UriFor(fileName);
        _ctx.LastUri = uri;
        _ctx.LastDocumentText = content;
        _ctx.LastVersion = 1;
        _ctx.Harness.Client.OpenDocument(uri, 1, content);
        _ctx.LastTokens = await _ctx.Harness.Client.RequestSemanticTokensAsync(uri);
    }

    [When(@"the feature file ""(.*)"" is changed to")]
    public async Task WhenTheFeatureFileIsChangedTo(string fileName, string content)
    {
        var uri = _ctx.UriFor(fileName);
        _ctx.LastUri = uri;
        _ctx.LastDocumentText = content;
        _ctx.LastVersion += 1;
        _ctx.Harness.Client.ChangeDocument(uri, _ctx.LastVersion, content);
        _ctx.LastTokens = await _ctx.Harness.Client.RequestSemanticTokensAsync(uri);
    }

    [When(@"the feature file ""(.*)"" is closed")]
    public void WhenTheFeatureFileIsClosed(string fileName)
        => _ctx.Harness.Client.CloseDocument(_ctx.UriFor(fileName));

    [When("the semantic tokens are requested again")]
    public async Task WhenTheSemanticTokensAreRequestedAgain()
        => _ctx.LastTokens = await _ctx.Harness.Client.RequestSemanticTokensAsync(_ctx.LastUri!);

    [When("the semantic tokens are requested once")]
    public async Task WhenTheSemanticTokensAreRequestedOnce()
        => _ctx.LastTokens = await _ctx.Harness.Client.RequestSemanticTokensAsync(_ctx.LastUri!);

    [When("the semantic tokens for the whole-document range are requested")]
    public async Task WhenTheSemanticTokensForTheWholeDocumentRangeAreRequested()
    {
        var lineCount = _ctx.LastDocumentText!.Split('\n').Length;
        var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, lineCount, 0);
        _ctx.LastTokens = await _ctx.Harness.Client.RequestSemanticTokensRangeAsync(_ctx.LastUri!, range);
    }

    /// <summary>
    /// Announces the scenario's single default project, rooted at the workspace folder. The
    /// trailing file name is the feature file the scenario goes on to open; it plays no part in
    /// the notification and is kept because it reads well in the scenarios that use this step.
    /// Scenarios that need more than one project use "the project ... is announced in folder ..."
    /// instead.
    /// </summary>
    [When(@"the project is announced with output assembly ""([^""]*)"" for ""([^""]*)""")]
    public void WhenTheProjectIsAnnounced(string outputAssembly, string fileName)
        => AnnounceProject(
            _ctx.RegisterProject(LspScenarioContext.DefaultProjectFileName),
            outputAssembly);

    /// <summary>
    /// Announces the default project with a NuGet package reference. The only consumer that cares
    /// is F26's test-target resolver, which detects the test framework from package ids to know
    /// which row-test attribute to count on a generated Scenario Outline method.
    /// </summary>
    [When(@"the project is announced with output assembly ""([^""]*)"" for ""([^""]*)"" referencing package ""([^""]*)""")]
    public void WhenTheProjectIsAnnouncedReferencingPackage(
        string outputAssembly, string fileName, string packageId)
        => AnnounceProject(
            _ctx.RegisterProject(LspScenarioContext.DefaultProjectFileName, packageIds: new[] { packageId }),
            outputAssembly);

    /// <summary>
    /// Announces a named project rooted at its own workspace-relative sub-folder — the shape a
    /// membership scenario needs, where two projects can each claim the same physical file and a
    /// file at the workspace root sits outside both.
    /// </summary>
    [When(@"the project ""([^""]*)"" is announced in folder ""([^""]*)""")]
    public void WhenTheNamedProjectIsAnnouncedInFolder(string projectFileName, string projectFolder)
        => AnnounceProject(_ctx.RegisterProject(projectFileName, projectFolder), outputAssembly: null);

    [When(@"the project ""([^""]*)"" is announced in folder ""([^""]*)"" with output assembly ""([^""]*)""")]
    public void WhenTheNamedProjectIsAnnouncedInFolderWithOutputAssembly(
        string projectFileName, string projectFolder, string outputAssembly)
        => AnnounceProject(_ctx.RegisterProject(projectFileName, projectFolder), outputAssembly);

    [When(@"the project ""([^""]*)"" is announced in folder ""([^""]*)"" targeting ""([^""]*)""")]
    public void WhenTheNamedProjectIsAnnouncedInFolderTargeting(
        string projectFileName, string projectFolder, string targetFrameworkMoniker)
        => AnnounceProject(
            _ctx.RegisterProject(projectFileName, projectFolder, targetFrameworkMoniker),
            outputAssembly: null);

    [When(@"the project is unloaded")]
    public void WhenTheProjectIsUnloaded()
        => WhenTheNamedProjectIsUnloaded(LspScenarioContext.DefaultProjectFileName);

    [When(@"the project ""([^""]*)"" is unloaded")]
    public void WhenTheNamedProjectIsUnloaded(string projectFileName)
        => _ctx.Harness.Client.SendProjectUnloaded(new
        {
            projectFile = _ctx.GetProject(projectFileName).ProjectFile
        });

    /// <summary>
    /// Sends <c>reqnroll/projectLoaded</c> for an already-registered project. An unspecified
    /// output assembly defaults to <c>bin/Debug/&lt;project&gt;.dll</c> under the project folder;
    /// it need not exist on disk, because the specs that use it get their bindings from the
    /// Roslyn live path rather than from connector discovery.
    /// </summary>
    private void AnnounceProject(LspScenarioContext.SpecProject project, string? outputAssembly)
    {
        outputAssembly ??= Path.Combine(
            "bin", "Debug", Path.GetFileNameWithoutExtension(project.ProjectFile) + ".dll");

        _ctx.Harness.Client.SendProjectLoaded(new
        {
            workspaceFolder = _ctx.WorkspaceFolder,
            projectFile = project.ProjectFile,
            projectFolder = project.ProjectFolder,
            outputAssemblyPath = Path.IsPathRooted(outputAssembly)
                ? outputAssembly
                : Path.Combine(project.ProjectFolder, outputAssembly),
            targetFrameworkMoniker = project.TargetFrameworkMoniker,
            packageReferences = project.PackageIds
                .Select(id => new { packageId = id, version = "", installPath = "" })
                .ToArray()
        });
    }

    /// <summary>
    /// Sends a <c>reqnroll/projectFiles</c> baseline notification that includes every file
    /// listed in the Reqnroll table.  The table must have columns <c>path</c> and <c>role</c>
    /// (Feature | Binding).  Paths are relative to <see cref="LspScenarioContext.WorkspaceFolder"/>.
    /// </summary>
    [When(@"the project files baseline is announced for ""([^""]*)"" with")]
    public void WhenTheProjectFilesBaselineIsAnnounced(string projectFileName, Table table)
    {
        var project = _ctx.GetProject(projectFileName);

        _ctx.Harness.Client.SendProjectFiles(new
        {
            projectFile = project.ProjectFile,
            targetFrameworkMoniker = project.TargetFrameworkMoniker,
            kind  = 0,    // Baseline
            files = ToFileEntries(table, added: true)
        });
    }

    /// <summary>
    /// Sends a <c>reqnroll/projectFiles</c> delta notification removing the given file from the
    /// project's membership index -- the VS-side notification path for a file deletion/exclusion
    /// (issue #94), distinct from the VS Code <c>workspace/didChangeWatchedFiles</c> path exercised
    /// by <see cref="CSharpBindingSteps.WhenTheCsharpFileIsDeleted"/>. The table must have columns
    /// <c>path</c> and <c>role</c> (Feature | Binding). Paths are relative to
    /// <see cref="LspScenarioContext.WorkspaceFolder"/>.
    /// </summary>
    [When(@"the project files delta removes files for ""([^""]*)"" with")]
    public async Task WhenTheProjectFilesDeltaRemoves(string projectFileName, Table table)
    {
        var project = _ctx.GetProject(projectFileName);

        _ctx.Harness.Client.SendProjectFiles(new
        {
            projectFile = project.ProjectFile,
            targetFrameworkMoniker = project.TargetFrameworkMoniker,
            kind  = 1,    // Delta
            files = ToFileEntries(table, added: false)
        });

        // Allow the server to process the notification, purge the removed binding file's
        // entries from the registry, and re-parse open feature files before the next request.
        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a <c>reqnroll/projectFiles</c> delta notification adding files to the project's
    /// membership index — the path an IDE glue layer takes when a file is re-included in the
    /// project without a full re-send, which the design requires to restore that file's ownership
    /// (and with it its binding-dependent features).
    /// </summary>
    [When(@"the project files delta adds files for ""([^""]*)"" with")]
    public async Task WhenTheProjectFilesDeltaAdds(string projectFileName, Table table)
    {
        var project = _ctx.GetProject(projectFileName);

        _ctx.Harness.Client.SendProjectFiles(new
        {
            projectFile = project.ProjectFile,
            targetFrameworkMoniker = project.TargetFrameworkMoniker,
            kind  = 1,    // Delta
            files = ToFileEntries(table, added: true)
        });

        await Task.Delay(300).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a file into the workspace without opening it over LSP. Needed by features that read
    /// from disk rather than from the document buffer — F26's resolver parses the generated
    /// <c>&lt;feature&gt;.feature.cs</c> code-behind, which in a real project is a build output no
    /// editor has open.
    /// </summary>
    [StepDefinition(@"the file ""([^""]*)"" exists on disk with")]
    public async Task GivenTheFileExistsOnDiskWith(string fileName, string content)
    {
        await _ctx.EnsureStartedAsync().ConfigureAwait(false);
        var path = _ctx.PathFor(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content).ConfigureAwait(false);
    }

    // ── Then: handshake / capabilities ──────────────────────────────────────────

    [Then("the server advertises a semantic tokens provider")]
    public void ThenTheServerAdvertisesASemanticTokensProvider()
        => GetLegend().Should().NotBeNull();

    [Then("the server advertises range support for semantic tokens")]
    public void ThenTheServerAdvertisesRangeSupportForSemanticTokens()
    {
        var provider = _ctx.Harness.ServerInitializeResult.Capabilities.SemanticTokensProvider;
        provider.Should().NotBeNull("the server should advertise a semantic tokens provider");
        provider!.Range!.IsBool.Should().BeTrue("Range is declared as a plain bool flag, not the object-options form");
        provider.Range!.Bool.Should().BeTrue(
            "VS Code and Rider both support textDocument/semanticTokens/range (issue #123); " +
            "the handler is wired up via manual routing in LanguageServerOptionsExtensions");
    }

    [Then("the server does not advertise pull support for semantic tokens")]
    public void ThenTheServerDoesNotAdvertisePullSupportForSemanticTokens()
    {
        var provider = _ctx.Harness.ServerInitializeResult.Capabilities.SemanticTokensProvider;
        provider.Should().NotBeNull(
            "the legend must still be advertised even when pull support is withheld -- " +
            "SemanticTokensClassificationInterceptor reads it from this same initialize response");
        (provider!.Full?.Bool ?? false).Should().BeFalse(
            "Visual Studio's built-in LSP client can't render our custom token types and has been " +
            "observed pulling anyway when full support is advertised, duplicating the expensive " +
            "encode the reqnroll/semanticTokens push path already paid for");
        (provider.Range?.Bool ?? false).Should().BeFalse(
            "same reasoning as Full above -- VS relies solely on the push mechanism");
    }

    [Then("the server advertises a semantic token legend")]
    public void ThenTheServerAdvertisesASemanticTokenLegend()
    {
        var provider = _ctx.Harness.ServerInitializeResult.Capabilities.SemanticTokensProvider;
        provider.Should().NotBeNull();
        provider!.Legend.TokenTypes.Should().NotBeEmpty(
            "the VS extension's SemanticTokensClassificationInterceptor decodes reqnroll/semanticTokens " +
            "push notifications using this legend -- it carries no legend of its own");
    }

    [Then("the server statically advertises textDocumentSync with full sync and openClose")]
    public void ThenTheServerStaticallyAdvertisesTextDocumentSync()
    {
        var ts = _ctx.Harness.ServerInitializeResult.Capabilities.TextDocumentSync;
        ts.Should().NotBeNull(
            "non-VS clients need a static textDocumentSync entry to bootstrap their " +
            "DidChangeTextDocument infrastructure; without it, dynamic registration is silently ignored");
        ts!.HasOptions.Should().BeTrue(
            "the static entry must be TextDocumentSyncOptions (not just a kind enum) so that " +
            "vscode-languageclient v10 recognises it and wires up its DidChangeTextDocument feature");
        ts.Options!.OpenClose.Should().BeTrue(
            "OpenClose=true is set explicitly in the static response — its presence in " +
            "ServerSettings confirms the static entry was included in the InitializeResult");
    }

    [Then("the server advertises renameProvider with prepareProvider")]
    public void ThenTheServerAdvertisesRenameProvider()
    {
        var rename = _ctx.Harness.ServerInitializeResult.Capabilities.RenameProvider;
        rename.Should().NotBeNull(
            "every client needs a static renameProvider declaration to activate F2 rename (issue #33)");
        rename!.IsValue.Should().BeTrue(
            "renameProvider should be advertised with static options, not just a boolean flag");
        rename.Value!.PrepareProvider.Should().BeTrue(
            "prepareProvider=true is required so the client sends textDocument/prepareRename before rename");
    }

    [Then("the server statically advertises an inlayHintProvider")]
    public void ThenTheServerStaticallyAdvertisesInlayHintProvider()
    {
        var inlayHint = _ctx.Harness.ServerInitializeResult.Capabilities.InlayHintProvider;
        inlayHint.Should().NotBeNull(
            "inlayHint/foldingRange must be declared statically — dynamic client/registerCapability " +
            "races VS Code's restore of previously-open .feature tabs on window load, and losing that " +
            "race silently disables the provider for the rest of the session");
        inlayHint!.IsValue.Should().BeTrue(
            "inlayHintProvider should be advertised with static options, not just a boolean flag");
    }

    [Then("the server statically advertises a foldingRangeProvider")]
    public void ThenTheServerStaticallyAdvertisesFoldingRangeProvider()
        => _ctx.Harness.ServerInitializeResult.Capabilities.FoldingRangeProvider
            .Should().NotBeNull(
                "inlayHint/foldingRange must be declared statically — dynamic client/registerCapability " +
                "races VS Code's restore of previously-open .feature tabs on window load, and losing " +
                "that race silently disables the provider for the rest of the session");

    [Then("the semantic tokens legend includes the token types")]
    public void ThenTheLegendIncludesTokenTypes(Table table)
    {
        var legend = GetLegend();
        var advertised = legend.TokenTypes.Select(t => t.ToString()).ToList();
        foreach (var row in table.Rows)
            advertised.Should().Contain(row["tokenType"]);
    }

    // ── Then: tokens ────────────────────────────────────────────────────────────

    [Then(@"the semantic tokens include a ""(.*)"" token for ""(.*)""")]
    public void ThenTheSemanticTokensIncludeATokenFor(string tokenType, string text)
    {
        var tokens = DecodeLast();
        tokens.Should().Contain(
            t => string.Equals(t.TokenType, tokenType, StringComparison.OrdinalIgnoreCase)
                 && t.Text.Trim() == text,
            $"a '{tokenType}' token covering '{text}' should be present. Got: " +
            string.Join(", ", tokens.Select(t => $"{t.TokenType}:'{t.Text}'")));
    }

    [Then(@"the semantic tokens do not include any ""(.*)"" token")]
    public void ThenTheSemanticTokensDoNotIncludeAnyTokenOfType(string tokenType)
    {
        var tokens = DecodeLast();
        tokens.Should().NotContain(
            t => string.Equals(t.TokenType, tokenType, StringComparison.OrdinalIgnoreCase));
    }

    [Then(@"the semantic tokens include a ""(.*)"" token with the ""(.*)"" modifier for ""(.*)""")]
    public void ThenTokenWithModifierFor(string tokenType, string modifier, string text)
    {
        var tokens = DecodeLast();
        tokens.Should().Contain(
            t => string.Equals(t.TokenType, tokenType, StringComparison.OrdinalIgnoreCase)
                 && t.Text.Trim() == text
                 && t.Modifiers.Any(m => string.Equals(m, modifier, StringComparison.OrdinalIgnoreCase)),
            $"a '{tokenType}'+'{modifier}' token covering '{text}' should be present");
    }

    [Then("the semantic tokens are non-overlapping")]
    public void ThenTheSemanticTokensAreNonOverlapping()
    {
        var tokens = DecodeLast().OrderBy(t => t.Line).ThenBy(t => t.StartChar).ToList();
        for (int i = 1; i < tokens.Count; i++)
        {
            var prev = tokens[i - 1];
            var cur = tokens[i];
            if (cur.Line != prev.Line) continue;
            (prev.StartChar + prev.Length).Should().BeLessThanOrEqualTo(
                cur.StartChar,
                $"token '{prev.Text}' ({prev.StartChar}+{prev.Length}) must not overlap '{cur.Text}' ({cur.StartChar})");
        }
    }

    [Then("no semantic tokens are returned")]
    public void ThenNoSemanticTokensAreReturned()
        => (_ctx.LastTokens is null || _ctx.LastTokens.Data.Length == 0).Should().BeTrue(
            "the document has no tags (e.g. after close), so no tokens should be produced");

    [Then("the server requests a semantic tokens refresh")]
    public async Task ThenTheServerRequestsASemanticTokensRefresh()
        => (await _ctx.Harness.WaitForRefreshAsync(minCount: 1)).Should().BeTrue(
            "the server should ask the client to refresh semantic tokens after a re-parse");

    [Then(@"the client receives a semantic tokens push for ""(.*)""")]
    public async Task ThenClientReceivesPushFor(string fileName)
        => (await _ctx.Harness.WaitForPushAsync(
                uri => uri.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)))
            .Should().BeTrue(
                $"the server should push a reqnroll/semanticTokens notification for '{fileName}' to the VS client");

    [Then("the client receives no semantic tokens push")]
    public async Task ThenClientReceivesNoPush()
    {
        // The push (if any) fires immediately after the match cache changes — which also drives the
        // (debounced, 500 ms) refresh request. Wait past that window, then assert nothing was pushed.
        await Task.Delay(1500);
        _ctx.Harness.SemanticTokenPushes.Should().BeEmpty(
            "non-Visual-Studio clients pull semantic tokens themselves; the server must not push to them");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps a <c>| path | role |</c> table to <c>reqnroll/projectFiles</c> file entries. Paths
    /// are workspace-relative, so a multi-project scenario writes them with the project's own
    /// folder as a prefix ("Linking/Steps.cs") and a single-project scenario keeps writing them
    /// bare ("Steps.cs").
    /// </summary>
    private object[] ToFileEntries(Table table, bool added)
        => table.Rows.Select(r => (object)new
        {
            path  = _ctx.PathFor(r["path"]),
            role  = string.Equals(r["role"], "Feature", StringComparison.OrdinalIgnoreCase) ? 0 : 1,
            added
        }).ToArray();

    private SemanticTokensLegend GetLegend()
    {
        var provider = _ctx.Harness.ServerInitializeResult.Capabilities.SemanticTokensProvider;
        provider.Should().NotBeNull("the server should advertise a semantic tokens provider");
        return provider!.Legend;
    }

    private IReadOnlyList<DecodedToken> DecodeLast()
    {
        _ctx.LastTokens.Should().NotBeNull("semantic tokens should have been returned");
        return SemanticTokenDecoder.Decode(_ctx.LastTokens!, GetLegend(), _ctx.LastDocumentText);
    }
}
