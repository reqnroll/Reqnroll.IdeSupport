using System.Text.RegularExpressions;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Matching;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.Workspace;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Features.SemanticTokens;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Tagging;
using Reqnroll.IdeSupport.LSP.Server.Workspace;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Tagging;

/// <summary>
/// The real tagger, buffer service and token service together: tags derived from a newer registry
/// reach the client without anything telling the token cache to evict, because the cache is keyed on
/// the stamp the tags carry. Editing a step definition recolours an open feature file this way.
/// </summary>
public class SemanticTokensStampEndToEndTests
{
    private const string Feature = "Feature: F\n\nScenario: S\n    Given my step\n";

    private readonly IIdeSupportLogger             _logger         = Substitute.For<IIdeSupportLogger>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IDocumentGenerations          _generations    = new DocumentGenerations();
    private readonly IDocumentBufferService        _buffers        = new DocumentBufferService();
    private readonly DocumentUri                   _uri            = DocumentUri.FromFileSystemPath("/workspace/recolour.feature");

    private static ProjectBindingRegistry RegistryWith(params ProjectStepDefinitionBinding[] bindings) =>
        new(bindings, Array.Empty<ProjectHookBinding>(), 0);

    private static ProjectStepDefinitionBinding MyStepBinding() =>
        new(ScenarioBlock.Given, new Regex("^my step$"), null,
            new ProjectBindingImplementation("MyStep", null, new SourceLocation("Steps.cs", 5, 1)));

    [Fact]
    public async Task A_registry_that_gains_the_step_definition_recolours_the_unchanged_open_document()
    {
        var tagger = new GherkinDocumentTaggerService(
            _buffers, TaggerTestParser.Create(_logger), _registryLookup,
            new BindingMatchService(_generations, _logger), _scopeManager, _logger, new FileSystemForIDE(),
            _generations, new ProjectConfigGenerations());
        var tokens = new SemanticTokensService(_buffers, _logger);

        _buffers.Update(_uri, 1, Feature, _generations.Bump(_uri.ToString()));
        _registryLookup.GetRegistryForUri(_uri).Returns(RegistryWith());
        await tagger.ParseAsync(_uri, 1);
        var undefined = await tokens.GetSemanticTokensAsync(_uri, 1);

        // A step definition is added: the same text and client version, parsed against a newer registry.
        _registryLookup.GetRegistryForUri(_uri).Returns(RegistryWith(MyStepBinding()));
        await tagger.ParseAsync(_uri, 1);
        var defined = await tokens.GetSemanticTokensAsync(_uri, 1);

        defined.Should().NotBeSameAs(undefined);
        defined!.Data.Should().NotEqual(undefined!.Data, "the step is no longer an undefined step");
    }
}
