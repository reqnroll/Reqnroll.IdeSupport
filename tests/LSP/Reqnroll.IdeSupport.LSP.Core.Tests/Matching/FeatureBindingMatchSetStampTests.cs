using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Tests.Matching;

/// <summary>
/// <see cref="FeatureBindingMatchSet.Stamp"/>: the stamp overloads record every input version, and
/// the registry-version-only overloads keep working with generations of 0.
/// </summary>
public class FeatureBindingMatchSetStampTests
{
    private const string Uri = "file:///c:/proj/feature1.feature";

    private static readonly ProjectOwner Owner = new(@"c:\proj\Proj.csproj", "net10.0");

    [Fact]
    public void Constructor_with_a_stamp_records_it_and_reports_its_registry_version()
    {
        var stamp = new DerivationStamp(ContentGeneration: 4, ConfigGeneration: 2, RegistryVersion: 9);

        var set = new FeatureBindingMatchSet(Uri, Owner, documentVersion: 3, stamp, Array.Empty<StepBindingMatch>());

        set.Stamp.Should().Be(stamp);
        set.RegistryVersion.Should().Be(9);
        set.DocumentVersion.Should().Be(3);
    }

    [Fact]
    public void Constructor_with_only_a_registry_version_has_generations_of_zero()
    {
        var set = new FeatureBindingMatchSet(Uri, Owner, documentVersion: null, registryVersion: 9, Array.Empty<StepBindingMatch>());

        set.Stamp.Should().Be(new DerivationStamp(0, 0, 9));
        set.RegistryVersion.Should().Be(9);
    }

    [Fact]
    public void FromTags_with_a_stamp_records_it()
    {
        var stamp = new DerivationStamp(4, 2, 9);

        var set = FeatureBindingMatchSet.FromTags(Uri, documentVersion: null, stamp, Array.Empty<Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.IdeSupportTag>(), Owner);

        set.Stamp.Should().Be(stamp);
    }

    [Fact]
    public void FromTags_with_only_a_registry_version_has_generations_of_zero()
    {
        var set = FeatureBindingMatchSet.FromTags(Uri, documentVersion: null, registryVersion: 9, Array.Empty<Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin.IdeSupportTag>(), Owner);

        set.Stamp.Should().Be(new DerivationStamp(0, 0, 9));
    }
}
