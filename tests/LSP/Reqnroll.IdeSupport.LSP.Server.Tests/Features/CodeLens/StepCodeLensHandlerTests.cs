using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Reqnroll.IdeSupport.Common.Logging;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Documents;


using Reqnroll.IdeSupport.LSP.Core.Matching;



using Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;
using Reqnroll.IdeSupport.LSP.Server.Features.CodeLens;
using Reqnroll.IdeSupport.LSP.Server.Hosting;
using Reqnroll.IdeSupport.LSP.Server.Registry;
using Reqnroll.IdeSupport.LSP.Server.Workspace;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Features.CodeLens;

public class StepCodeLensHandlerTests
{
    private readonly IBindingMatchService          _matchService   = Substitute.For<IBindingMatchService>();
    private readonly ILspWorkspaceScopeManager     _scopeManager   = Substitute.For<ILspWorkspaceScopeManager>();
    private readonly IProjectBindingRegistryLookup _registryLookup = Substitute.For<IProjectBindingRegistryLookup>();
    private readonly IIdeSupportLogger               _logger         = Substitute.For<IIdeSupportLogger>();

    private static readonly DocumentUri CsUri      = DocumentUri.FromFileSystemPath("/workspace/Steps.cs");
    private static readonly DocumentUri FeatureUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public StepCodeLensHandlerTests()
    {
        _scopeManager.ResolveOwners(Arg.Any<DocumentUri>())
                     .Returns(Array.Empty<LspReqnrollProject>());
        _registryLookup.GetRegistryForUri(Arg.Any<DocumentUri>())
                       .Returns(ProjectBindingRegistry.Invalid);
    }

    /// <summary>
    /// Builds the handler for a client identified by <paramref name="ide"/>. The default
    /// (<c>supportsCodeLensResolve: false</c>) is what EVERY shipped client gets today — the
    /// <c>codeLens/resolve</c> allowlist in <see cref="ClientIdeContext"/> is empty, so the eager
    /// path is the production path for VS, VS Code and Rider alike (issue #471).
    /// Pass <c>supportsCodeLensResolve: true</c> to exercise the deferred branch, which is real
    /// code kept ready for the first client that implements the resolve round trip.
    /// </summary>
    private StepCodeLensHandler CreateSut(string ide = "visualstudio", bool supportsCodeLensResolve = false) =>
        new(_matchService, _scopeManager, _registryLookup,
            new ClientIdeContext(ide, supportsCodeLensResolve), _logger);

    private static CodeLensParams RequestFor(DocumentUri uri) =>
        new() { TextDocument = new TextDocumentIdentifier { Uri = uri } };

    private static ProjectBindingRegistry MakeRegistry(params ProjectStepDefinitionBinding[] bindings)
    {
        var allBindings = bindings as IEnumerable<ProjectStepDefinitionBinding>;
        return new ProjectBindingRegistry(allBindings, Array.Empty<ProjectHookBinding>(), projectHash: 1);
    }

    /// <summary>Creates a real (disposable) <see cref="LspReqnrollProject"/> for tests that need a genuine <see cref="ProjectOwner"/>-bearing project, not just a substitute.</summary>
    private static LspReqnrollProject MakeProject(string projectFile) =>
        new(new ReqnrollProjectLoadedParams
            {
                WorkspaceFolder        = "/workspace",
                ProjectFile            = projectFile,
                ProjectFolder          = System.IO.Path.GetDirectoryName(projectFile)!,
                OutputAssemblyPath     = "/workspace/bin/Project.dll",
                TargetFrameworkMoniker = "net8.0"
            },
            new LspIdeScope(Substitute.For<IIdeSupportLogger>()));

    // ── Non-.cs URI ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_non_cs_uri_returns_empty_array()
    {
        var result = await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_non_cs_uri_does_not_query_registry()
    {
        await CreateSut().HandleAsync(RequestFor(FeatureUri), CancellationToken.None);

        _registryLookup.DidNotReceive().GetRegistryForUri(Arg.Any<DocumentUri>());
    }

    // ── Invalid / empty registry ─────────────────────────────────────────────

    [Fact]
    public async Task Handle_invalid_registry_returns_empty_array()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Should().BeEmpty();
    }

    // ── File-path matching ────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_binding_in_different_file_is_excluded()
    {
        var otherPath = "/workspace/OtherSteps.cs";
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(otherPath).AtLine(5).AtColumn(1)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result!.Should().BeEmpty();
    }

    // ── Usage counts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_single_binding_with_zero_usages_returns_one_lens_with_correct_title()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(csPath).AtLine(5).AtColumn(1)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result!.Should().ContainSingle();
        result![0].Command!.Title.Should().Be("0 step usages");
    }

    [Fact]
    public async Task Handle_single_binding_with_one_usage_returns_singular_title()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(csPath).AtLine(5).AtColumn(1)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { StepBindingMatchBuilder.Create(FeatureUri) });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result![0].Command!.Title.Should().Be("1 step usage");
    }

    [Fact]
    public async Task Handle_single_binding_with_multiple_usages_returns_plural_title()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(csPath).AtLine(5).AtColumn(1)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[]
                     {
                         StepBindingMatchBuilder.Create(FeatureUri),
                         StepBindingMatchBuilder.Create(FeatureUri),
                         StepBindingMatchBuilder.Create(FeatureUri),
                     });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result![0].Command!.Title.Should().Be("3 step usages");
    }

    // ── Range / position ─────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_lens_range_is_at_attribute_line_zero_based()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(csPath).AtLine(10).AtColumn(5)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        // SourceFileLine 10 → LSP line 9; SourceFileColumn 5 → LSP character 4
        var range = result![0].Range!;
        range.Start.Line.Should().Be(9);
        range.Start.Character.Should().Be(4);
        range.End.Line.Should().Be(9);
        range.End.Character.Should().Be(4);
    }

    // ── Command wiring ────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_lens_with_usages_uses_findStepUsages_command_name()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(csPath).AtLine(5).AtColumn(1)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { StepBindingMatchBuilder.Create(FeatureUri) });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result![0].Command!.Name.Should().Be("reqnroll.findStepUsages");
    }

    [Fact]
    public async Task Handle_lens_with_zero_usages_uses_noStepUsages_command_name()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create()
                                        .AtSourceFile(csPath).AtLine(5).AtColumn(1)
                                        .Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result![0].Command!.Name.Should().Be("reqnroll.noStepUsages");
    }

    // ── Multiple bindings ─────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_two_bindings_in_same_file_returns_two_lenses()
    {
        var csPath = CsUri.GetFileSystemPath()!;
        var b1 = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        var b2 = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(9).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(b1, b2));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result!.Should().HaveCount(2);
    }

    [Fact]
    public async Task Handle_duplicate_attribute_location_emits_only_one_lens()
    {
        var csPath = CsUri.GetFileSystemPath()!;
        // Same location -- e.g. the same physical attribute reported twice because the registry
        // saw it twice for linked files -- must collapse to one lens regardless of content.
        var b1 = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        var b2 = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(b1, b2));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result!.Should().ContainSingle("duplicate locations should be deduplicated");
    }

    /// <summary>
    /// Regression test for issue #552: a method carrying multiple binding attributes (e.g.
    /// <c>[When("...")]</c> + <c>[When("...")]</c> + <c>[Then("...")]</c> on one method) reports
    /// the identical (SourceFileLine, SourceFileColumn) for every attribute -- the shared method
    /// identifier position (see the anchor-fix remarks in StepCodeLensHandler, issue #484). That
    /// must still collapse to a SINGLE lens (matching the conventional "N references"-per-method
    /// CodeLens every client renders one of per line) whose count is the aggregate of every
    /// attribute's usages, looked up via the same location-based <c>FindUsages</c> overload Find
    /// Step Usages (FAR) already uses -- not one lens per attribute (an earlier version of this
    /// fix tried that; VS Code rendered three separate lenses side by side instead of one, and
    /// Rider's CodeVision engine silently dropped every lens but the first at that position) and
    /// not a per-attribute BindingId lookup (the original #552 bug, which undercounted to a single
    /// attribute's own usages instead of the method's total).
    /// </summary>
    [Fact]
    public async Task Handle_multiple_attributes_on_the_same_method_produce_one_lens_with_the_aggregated_count()
    {
        var csPath = CsUri.GetFileSystemPath()!;
        var whenA = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1)
            .WithStepType(ScenarioBlock.When).WithExpression("the two nums are confirmed {int}").Build();
        var whenB = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1)
            .WithStepType(ScenarioBlock.When).WithExpression("the result should be {int}").Build();
        var then = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1)
            .WithStepType(ScenarioBlock.Then).WithExpression("the result should be {int}").Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(whenA, whenB, then));

        // BindingMatchService.FindUsages(SourceLocation, ...) is what actually aggregates every
        // attribute anchored at this location; the substitute here just proves the handler trusts
        // that single aggregate call instead of querying per-attribute.
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[]
                     {
                         StepBindingMatchBuilder.Create(FeatureUri),
                         StepBindingMatchBuilder.Create(FeatureUri),
                         StepBindingMatchBuilder.Create(FeatureUri),
                     });

        var result = await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result!.Should().ContainSingle("one lens per method, not per attribute");
        result![0].Command!.Title.Should().Be("3 step usages");
    }

    // ── Project-owner filter ──────────────────────────────────────────────────

    [Fact]
    public async Task Handle_passes_null_project_filter_when_no_owners()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _scopeManager.ResolveOwners(CsUri).Returns(Array.Empty<LspReqnrollProject>());
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f => f == null));
    }

    // ── External-assembly usage scoping (issue #548) ──────────────────────────

    /// <summary>
    /// A project that references another Reqnroll-bearing project (a class library) discovers
    /// that library's bindings too via its own connector run -- the same transitive-discovery
    /// behaviour that produced duplicate Find Unused Step Definitions rows before BindingId
    /// normalization (issue #547). That referencing project's feature files are legitimate usage
    /// sites for the library's steps even though it doesn't "own" the .cs file that declares
    /// them, so the project filter passed to FindUsages must include it -- not just the .cs
    /// file's direct owner(s), which would always undercount to zero for a step used only from
    /// the referencing project.
    /// </summary>
    [Fact]
    public async Task Handle_expands_project_filter_to_projects_whose_registry_independently_reports_the_binding()
    {
        var csPath = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();

        var libraryProject = MakeProject("/workspace/MyLibrary/MyLibrary.csproj");
        var referencingProject = MakeProject("/workspace/ReferencingProject/ReferencingProject.csproj");

        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _scopeManager.ResolveOwners(CsUri).Returns(new[] { libraryProject });

        // The referencing project's own registry independently reports the very same binding
        // object -- same content AND same physical SourceLocation.SourceFile (csPath) -- the
        // transitively-discovered copy of the library's own file, not an unrelated project's own
        // copy of similar-looking source (see the negative case below, issue #552).
        var referencingProjectRegistry = MakeRegistry(binding);
        _registryLookup.GetAllRegistries().Returns(new[]
        {
            ("MyLibrary", new ProjectOwner(libraryProject.ProjectFullName, libraryProject.TargetFrameworkMoniker), MakeRegistry(binding)),
            ("ReferencingProject", new ProjectOwner(referencingProject.ProjectFullName, referencingProject.TargetFrameworkMoniker), referencingProjectRegistry),
        });

        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f =>
                f != null
                && f.Any(o => o.ProjectFile == libraryProject.ProjectFullName)
                && f.Any(o => o.ProjectFile == referencingProject.ProjectFullName)));

        libraryProject.Dispose();
        referencingProject.Dispose();
    }

    [Fact]
    public async Task Handle_does_not_expand_project_filter_to_projects_that_do_not_report_the_binding()
    {
        var csPath = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        var unrelatedBinding = StepBindingBuilder.Create()
            .AtSourceFile("/workspace/Unrelated/Other.cs").AtLine(1).AtColumn(1).Build();

        var libraryProject = MakeProject("/workspace/MyLibrary/MyLibrary.csproj");
        var unrelatedProject = MakeProject("/workspace/Unrelated/Unrelated.csproj");

        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _scopeManager.ResolveOwners(CsUri).Returns(new[] { libraryProject });
        _registryLookup.GetAllRegistries().Returns(new[]
        {
            ("MyLibrary", new ProjectOwner(libraryProject.ProjectFullName, libraryProject.TargetFrameworkMoniker), MakeRegistry(binding)),
            ("Unrelated", new ProjectOwner(unrelatedProject.ProjectFullName, unrelatedProject.TargetFrameworkMoniker), MakeRegistry(unrelatedBinding)),
        });
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f =>
                f != null
                && f.Any(o => o.ProjectFile == libraryProject.ProjectFullName)
                && !f.Any(o => o.ProjectFile == unrelatedProject.ProjectFullName)));

        libraryProject.Dispose();
        unrelatedProject.Dispose();
    }

    /// <summary>
    /// Regression test for the project-scoping half of issue #552: an unrelated project with no
    /// reference relationship at all to the .cs file's owner -- e.g. a parallel multi-targeted
    /// sibling project (a net481 copy of the same test suite) with its own independent physical
    /// copy of the same source -- must NOT widen the usage search, even though its copy of the
    /// method normalizes to the identical <see cref="BindingId"/>. Doing so doubled the VS
    /// step-usage CodeLens count for exactly this solution shape (Minimal + Minimalnet481, no
    /// project reference between them, both with their own copy of the same step definitions and
    /// feature files) live-tested against the fix. Only a project whose reported binding resolves
    /// to this EXACT .cs file -- the signature of a genuine transitive-reference discovery, issue
    /// #548 -- may be added; see the positive case above.
    /// </summary>
    [Fact]
    public async Task Handle_does_not_expand_project_filter_to_a_project_with_its_own_copy_of_the_same_source()
    {
        var csPath = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();

        // Same content as `binding` (same synthesized method name/type/expression, so the same
        // BindingId) but a genuinely different physical file -- not a copy discovered through a
        // reference to `directOwner`, just a coincidentally identical sibling project.
        var siblingCopy = StepBindingBuilder.Create()
            .AtSourceFile("/workspace/Siblingnet481/Steps.cs").AtLine(5).AtColumn(1).Build();

        var directOwner  = MakeProject("/workspace/Direct/Direct.csproj");
        var siblingOwner = MakeProject("/workspace/Siblingnet481/Siblingnet481.csproj");

        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _scopeManager.ResolveOwners(CsUri).Returns(new[] { directOwner });
        _registryLookup.GetAllRegistries().Returns(new[]
        {
            ("Direct",  new ProjectOwner(directOwner.ProjectFullName, directOwner.TargetFrameworkMoniker),   MakeRegistry(binding)),
            ("Sibling", new ProjectOwner(siblingOwner.ProjectFullName, siblingOwner.TargetFrameworkMoniker), MakeRegistry(siblingCopy)),
        });
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        _matchService.Received(1).FindUsages(
            Arg.Any<SourceLocation>(),
            Arg.Is<IReadOnlyCollection<ProjectOwner>?>(f =>
                f != null
                && f.Any(o => o.ProjectFile == directOwner.ProjectFullName)
                && !f.Any(o => o.ProjectFile == siblingOwner.ProjectFullName)));

        directOwner.Dispose();
        siblingOwner.Dispose();
    }

    // ── Usage lookup is by location, not per-attribute BindingId (issue #552) ────

    [Fact]
    public async Task Handle_passes_the_bindings_location_to_FindUsages()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(7).AtColumn(3).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));

        SourceLocation? captured = null;
        _matchService.FindUsages(Arg.Do<SourceLocation>(loc => captured = loc),
                                 Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(Array.Empty<StepBindingMatch>());

        await CreateSut().HandleAsync(RequestFor(CsUri), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.SourceFile.Should().Be(csPath);
        captured.SourceFileLine.Should().Be(7);
        captured.SourceFileColumn.Should().Be(3);
    }

    // ── Deferred resolve (allowlisted resolve-capable clients only) ───────────

    /// <summary>
    /// The regression guard for issue #471's final-review fix: the deferred path must NOT be
    /// active for any client the repo actually ships. Every one of these goes through the
    /// production <see cref="ClientIdeContext"/> constructor, i.e. the real allowlist lookup —
    /// so this fails the moment someone adds an entry without also shipping client-side
    /// <c>resolveCodeLens</c> support.
    /// </summary>
    [Theory]
    [InlineData("visualstudio")]
    [InlineData("vscode")]
    [InlineData("rider")]
    [InlineData(null)]
    public async Task Handle_computes_every_lens_eagerly_for_all_shipped_clients(string? ide)
    {
        new ClientIdeContext(ide).SupportsCodeLensResolve.Should()
            .BeFalse("no shipped client implements the codeLens/resolve round trip yet");

        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { StepBindingMatchBuilder.Create(FeatureUri) });

        var sut = new StepCodeLensHandler(
            _matchService, _scopeManager, _registryLookup, new ClientIdeContext(ide), _logger);
        var result = await sut.HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result![0].Command!.Title.Should().Be("1 step usage");
        result[0].Data.Should().BeNull();
    }

    [Fact]
    public async Task Handle_resolve_capable_client_returns_placeholder_lens_without_calling_FindUsages()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));

        var result = await CreateSut(supportsCodeLensResolve: true)
            .HandleAsync(RequestFor(CsUri), CancellationToken.None);

        result![0].Command.Should().BeNull();
        result[0].Data.Should().NotBeNull();
        _matchService.DidNotReceive().FindUsages(Arg.Any<BindingId>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
        _matchService.DidNotReceive().FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }

    [Fact]
    public async Task ResolveAsync_computes_the_command_from_the_lens_Data()
    {
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(Arg.Any<BindingId>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { StepBindingMatchBuilder.Create(FeatureUri) });

        var placeholder = (await CreateSut(supportsCodeLensResolve: true)
            .HandleAsync(RequestFor(CsUri), CancellationToken.None))![0];
        placeholder.Command.Should().BeNull("the deferred branch must be the one under test here");

        var resolved = await CreateSut().ResolveAsync(placeholder, CancellationToken.None);

        resolved.Command!.Title.Should().Be("1 step usage");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_zero_usages_when_the_binding_can_no_longer_be_found()
    {
        _registryLookup.GetRegistryForUri(CsUri).Returns(ProjectBindingRegistry.Invalid);
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(4, 0), new Position(4, 0)),
            Data = new JObject
            {
                ["kind"] = "stepUsage",
                ["uri"] = CsUri.ToString(),
                ["sourceFile"] = CsUri.GetFileSystemPath(),
                ["sourceLine"] = 5,
                ["sourceColumn"] = 1,
            }
        };

        var resolved = await CreateSut().ResolveAsync(lens, CancellationToken.None);

        resolved.Command!.Title.Should().Be("0 step usages");
        resolved.Command.Name.Should().Be("reqnroll.noStepUsages");
        resolved.Command.Arguments.Should().BeNull("a zero-usage lens must not be clickable");
    }

    [Fact]
    public async Task ResolveAsync_falls_back_to_a_non_actionable_lens_when_the_Data_is_malformed()
    {
        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(4, 0), new Position(4, 0)),
            Data = new JObject { ["kind"] = "stepUsage" } // no uri/sourceFile/line/column
        };

        var resolved = await CreateSut().ResolveAsync(lens, CancellationToken.None);

        resolved.Command!.Name.Should().Be("reqnroll.noStepUsages");
        resolved.Command.Arguments.Should().BeNull("no URI is known, so nothing may be clickable");
    }

    [Fact]
    public async Task ResolveAsync_resolves_from_bindingId_alone_with_no_sourceFile_line_or_column()
    {
        // Confirms the index-only resolve path (issue #471): a Data payload carrying only
        // bindingId+uri (no sourceFile/sourceLine/sourceColumn) must still resolve via the direct
        // reverse-index lookup, with zero location math.
        var csPath  = CsUri.GetFileSystemPath()!;
        var binding = StepBindingBuilder.Create().AtSourceFile(csPath).AtLine(5).AtColumn(1).Build();
        _registryLookup.GetRegistryForUri(CsUri).Returns(MakeRegistry(binding));
        _matchService.FindUsages(BindingId.For(binding), Arg.Any<IReadOnlyCollection<ProjectOwner>>())
                     .Returns(new[] { StepBindingMatchBuilder.Create(FeatureUri) });

        var lens = new global::OmniSharp.Extensions.LanguageServer.Protocol.Models.CodeLens
        {
            Range = new LspRange(new Position(4, 0), new Position(4, 0)),
            Data = new JObject
            {
                ["kind"]      = "stepUsage",
                ["uri"]       = CsUri.ToString(),
                ["bindingId"] = BindingId.For(binding).ToString(),
            }
        };

        var resolved = await CreateSut().ResolveAsync(lens, CancellationToken.None);

        resolved.Command!.Title.Should().Be("1 step usage");
        _matchService.DidNotReceive().FindUsages(Arg.Any<SourceLocation>(), Arg.Any<IReadOnlyCollection<ProjectOwner>>());
    }
}

// ── Test helpers ──────────────────────────────────────────────────────────────

file static class StepBindingBuilder
{
    public static Builder Create() => new();

    public sealed class Builder
    {
        private string        _file       = "/workspace/Steps.cs";
        private int            _line       = 5;
        private int            _col        = 1;
        private ScenarioBlock  _stepType   = ScenarioBlock.Given;
        private string?        _expression;

        public Builder AtSourceFile(string file)         { _file       = file;       return this; }
        public Builder AtLine(int line)                  { _line       = line;       return this; }
        public Builder AtColumn(int col)                 { _col        = col;        return this; }
        public Builder WithStepType(ScenarioBlock type)  { _stepType   = type;       return this; }
        public Builder WithExpression(string expression) { _expression = expression; return this; }

        public ProjectStepDefinitionBinding Build()
        {
            var impl = new ProjectBindingImplementation(
                "MyMethod_" + _line,
                parameterTypes: null,
                new SourceLocation(_file, _line, _col));
            return new ProjectStepDefinitionBinding(
                _stepType,
                new System.Text.RegularExpressions.Regex("^.*$"),
                scope: null,
                implementation: impl,
                specifiedExpression: _expression);
        }
    }
}

file static class StepBindingMatchBuilder
{
    private static readonly DocumentUri DefaultUri = DocumentUri.FromFileSystemPath("/workspace/test.feature");

    public static StepBindingMatch Create(DocumentUri? featureUri = null)
    {
        var uri      = featureUri ?? DefaultUri;
        var snapshot = new LspTextSnapshot(uri.ToString(), 1, "Feature: F\nScenario: S\n    Given step\n");
        var range    = GherkinRange.FromPoint(snapshot, 23, 4);
        return new StepBindingMatch(uri.ToString(), range, MatchResult.NoMatch, "Given", "S", "P");
    }
}
