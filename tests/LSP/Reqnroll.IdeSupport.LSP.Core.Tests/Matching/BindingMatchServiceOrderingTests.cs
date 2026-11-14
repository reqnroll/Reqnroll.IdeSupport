using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// Characterises the order in which <see cref="BindingMatchService.Store"/> accepts writes for one key.
/// Closed-file scans of one project are started by several triggers (a full registry replacement, the
/// debounced rescan after a patch, a configuration change) and are not serialised, so for one file the
/// result computed against an older registry can reach <c>Store</c> last.
/// </summary>
public class BindingMatchServiceOrderingTests
{
    private const string DocumentId = "file:///c:/proj/feature1.feature";

    private static readonly ProjectOwner Owner = new(@"c:\proj\Proj.csproj", "net10.0");

    private static ProjectBindingRegistry EmptyRegistry() =>
        new(Array.Empty<ProjectStepDefinitionBinding>(), Array.Empty<ProjectHookBinding>(), 0);

    [Fact(Skip = "Stale derived-state write is accepted until the versioned-derived-state store guard is enforced")]
    public void Closed_file_set_built_against_an_older_registry_does_not_replace_one_built_against_a_newer_registry()
    {
        var older = EmptyRegistry();
        var newer = EmptyRegistry();
        newer.Version.Should().BeGreaterThan(older.Version);
        var sut = new BindingMatchService(new DocumentGenerations(), Substitute.For<IIdeSupportLogger>());

        sut.Store(new FeatureBindingMatchSet(DocumentId, Owner, documentVersion: null, newer.Version, Array.Empty<StepBindingMatch>()));
        sut.Store(new FeatureBindingMatchSet(DocumentId, Owner, documentVersion: null, older.Version, Array.Empty<StepBindingMatch>()));

        sut.TryGet(new MatchSetKey(DocumentId, Owner), out var cached).Should().BeTrue();
        cached.RegistryVersion.Should().Be(newer.Version, "the set built against the newer registry must win");
    }
}
