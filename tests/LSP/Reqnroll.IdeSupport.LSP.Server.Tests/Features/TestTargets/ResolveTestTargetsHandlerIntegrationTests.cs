using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common;
using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.Common.ProjectSystem.Configuration;
using Reqnroll.IdeSupport.Common.Telemetry;
using Reqnroll.IdeSupport.LSP.Core.Documents;
using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Core.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Features.TestTargets;
using Reqnroll.IdeSupport.LSP.Server.Documents;
using Reqnroll.IdeSupport.LSP.Server.Protocol;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.TestTargets;

/// <summary>
/// Exercises <see cref="ResolveTestTargetsHandler"/> against a <em>real</em>
/// <see cref="ScenarioTestTargetResolver"/> and real <see cref="DeveroomTagParser"/>-produced tags,
/// rather than the mocked <c>IScenarioTestTargetResolver</c> the other tests in this folder use.
/// </summary>
/// <remarks>
/// Regression coverage for a bug found live-testing issue #262: the handler built its query
/// <c>GherkinRange</c> from a freshly-allocated <c>buffer.ToGherkinTextSnapshot()</c> instead of
/// reusing the snapshot instance <c>buffer.Tags</c> was already anchored to.
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Documents.GherkinRange.IntersectsWith"/> requires
/// reference-equal snapshots and throws otherwise — <c>ScenarioTestTargetResolver.FindScenarioTag</c>
/// hit that on every call, and the exception was silently swallowed by OmniSharp's request pipeline,
/// surfacing to every client as an empty (0-target) result with no visible error. Every existing
/// <c>ResolveTestTargetsHandlerTests</c> mocks <c>IScenarioTestTargetResolver</c> entirely, so none
/// of them exercised the real cross-layer snapshot plumbing that this bug lived in.
/// </remarks>
public class ResolveTestTargetsHandlerIntegrationTests : IDisposable
{
    private readonly IDocumentBufferService _bufferService = Substitute.For<IDocumentBufferService>();
    private readonly ILspWorkspaceScopeManager _scopeManager = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IIdeSupportLogger _logger = Substitute.For<IIdeSupportLogger>();
    private readonly List<string> _tempFeaturePaths = new();

    public void Dispose()
    {
        foreach (var path in _tempFeaturePaths)
        {
            try
            {
                if (File.Exists(path + ".cs"))
                    File.Delete(path + ".cs");
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    public ResolveTestTargetsHandlerIntegrationTests()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>()).Returns(Array.Empty<LspReqnrollProject>());
        _scopeManager.ResolvePrimaryOwner(Arg.Any<DocumentUri>()).Returns((LspReqnrollProject?)null);
    }

    private ResolveTestTargetsHandler CreateSut() =>
        new(_bufferService, new ScenarioTestTargetResolver(), _scopeManager, _logger);

    /// <summary>
    /// Parses <paramref name="text"/> the same way the live pipeline populates
    /// <c>DocumentBuffer.Tags</c> (via <see cref="DeveroomTagParser"/>), writes a matching generated
    /// <c>.feature.cs</c> fixture beside a temp <c>.feature</c> path, and registers both with the
    /// buffer service under that path's URI.
    /// </summary>
    private DocumentUri SetupRealBuffer(string text, string generatedCsContent)
    {
        var featurePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.feature");
        _tempFeaturePaths.Add(featurePath);
        File.WriteAllText(featurePath + ".cs", generatedCsContent);

        var uri = DocumentUri.FromFileSystemPath(featurePath);

        var parserLogger = Substitute.For<IIdeSupportLogger>();
        var parserTelemetry = Substitute.For<ITelemetryService>();
        var configProvider = Substitute.For<IDeveroomConfigurationProvider>();
        configProvider.GetConfiguration().Returns(new DeveroomConfiguration());
        var parser = new DeveroomTagParser(parserLogger, parserTelemetry, configProvider);
        var tags = parser.Parse(new LspTextSnapshot(uri.ToString(), 1, text), Core.Bindings.ProjectBindingRegistry.Invalid);

        var buffer = new DocumentBuffer(uri, 1, text, tags);
        DocumentBuffer? ignored;
        _bufferService.TryGet(uri, out ignored)
            .Returns(x =>
            {
                x[1] = buffer;
                return true;
            });

        return uri;
    }

    /// <summary>Makes <see cref="ILspWorkspaceScopeManager.ResolveOwners"/> report a project referencing <c>Reqnroll.xUnit</c>, so <c>TestFrameworkDetection</c> recognizes row-attribute parameterization for <paramref name="uri"/>.</summary>
    private void UseXUnitPackageReferenceFor(DocumentUri uri)
    {
        var project = new LspReqnrollProject(
            new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder = Path.GetTempPath(),
                ProjectFile = Path.Combine(Path.GetTempPath(), "Fixture.csproj"),
                ProjectFolder = Path.GetTempPath(),
                OutputAssemblyPath = Path.Combine(Path.GetTempPath(), "bin", "Fixture.dll"),
                TargetFrameworkMoniker = ".NETCoreApp,Version=v8.0",
                PackageReferences = new[] { new PackageReferenceInfo { PackageId = "Reqnroll.xUnit", Version = "3.0.0" } },
            },
            Substitute.For<IIdeScope>());

        _scopeManager.ResolveOwners(uri).Returns(new[] { project });
    }

    /// <summary>
    /// Builds a request range spanning most of <paramref name="line"/> (0-based) — a real
    /// <c>DocumentSymbol.SelectionRange</c> for a scenario is a real text span, not a zero-length
    /// point, and <see cref="Core.Documents.GherkinRange.IntersectsWith"/> treats a zero-length
    /// range touching another range's exact start boundary as non-intersecting (mirrors
    /// <c>SnapshotSpan</c> semantics) — a point exactly at the tag's start would misrepresent what
    /// the real client sends.
    /// </summary>
    private static ResolveTestTargetsParams RequestAt(DocumentUri uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Range = new LspRange(new Position(line, character), new Position(line, character + 8)),
        };

    [Fact]
    public async Task Handle_resolves_a_real_scenario_against_a_real_resolver_without_throwing_Async()
    {
        var text = "Feature: Calculator\nScenario: Add two numbers\n    Given a step\n";
        var generatedCs = """
            namespace Tests
            {
                public class CalculatorFeature
                {
                    public void AddTwoNumbers()
                    {
                    }
                }
            }
            """;
        var uri = SetupRealBuffer(text, generatedCs);

        // Line 1 (0-based) is "Scenario: Add two numbers".
        var result = await CreateSut().HandleAsync(RequestAt(uri, 1, 0), CancellationToken.None);

        var target = result.Targets.Should().ContainSingle().Subject;
        target.DeclaringTypeFullName.Should().Be("Tests.CalculatorFeature");
        target.MethodName.Should().Be("AddTwoNumbers");
    }

    [Fact]
    public async Task Handle_resolves_a_real_row_tests_outline_against_a_real_resolver_Async()
    {
        var text = """
            Feature: F
            Scenario Outline: Add numbers
                Given the first number is <a>
                And the second number is <b>
                Then the result is <c>

                Examples:
                    | a  | b  | c  |
                    | 1  | 2  | 3  |
                    | 4  | 5  | 9  |

            """;
        var generatedCs = """
            namespace Tests
            {
                public class FFeature
                {
                    [InlineData("1", "2", "3")]
                    [InlineData("4", "5", "9")]
                    public void AddNumbers()
                    {
                    }
                }
            }
            """;
        var uri = SetupRealBuffer(text, generatedCs);
        UseXUnitPackageReferenceFor(uri);

        // Line 1 (0-based) is "Scenario Outline: Add numbers".
        var result = await CreateSut().HandleAsync(RequestAt(uri, 1, 0), CancellationToken.None);

        result.Targets.Should().HaveCount(2);
        result.Targets.Should().OnlyContain(t => t.MethodName == "AddNumbers" && t.IsParameterized);
    }
}
