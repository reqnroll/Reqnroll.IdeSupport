using Reqnroll.IdeSupport.Common.Tests.TestHelpers;

namespace Reqnroll.IdeSupport.Common.Tests.ProjectSystem.Settings;

public class ReqnrollProjectSettingsProviderTests
{
    private const string ReqnrollPackagePath = @"X:\nuget-cache\reqnroll\2.4.1";
    private const string SpecFlowLegacyPackagePath = @"X:\nuget-cache\specflow\2.4.1";

    private readonly MockFileSystemForTests _fileSystem = new();

    public ReqnrollProjectSettingsProviderTests()
    {
        _fileSystem.AddDirectory(ReqnrollPackagePath);
        _fileSystem.AddDirectory(SpecFlowLegacyPackagePath);
    }

    // VoidProjectScope skips config-file loading entirely (InitializeConfiguration's
    // `_projectScope is VoidProjectScope` branch) and has empty ProjectFolder/OutputAssemblyPath,
    // so it isolates GetReqnrollSettings' package/output-folder resolution from the config-file
    // merge step without needing a real config file on a mock filesystem.
    private ReqnrollProjectSettingsProvider CreateSut()
    {
        var ideScope = Substitute.For<IIdeScope>();
        ideScope.FileSystem.Returns(_fileSystem);
        return new ReqnrollProjectSettingsProvider(new VoidProjectScope(ideScope));
    }

    [Fact]
    public void GetReqnrollSettings_returns_null_when_there_are_no_packages_and_no_output_folder()
    {
        var result = CreateSut().GetReqnrollSettings(Array.Empty<NuGetPackageReference>());

        result.Should().BeNull();
    }

    [Fact]
    public void GetReqnrollSettings_resolves_a_modern_Reqnroll_package_with_MsBuild_generation_and_CucumberExpression_traits()
    {
        var packages = new[]
        {
            new NuGetPackageReference("Reqnroll", new NuGetVersion("2.4.1", "2.4.1"), ReqnrollPackagePath)
        };

        var result = CreateSut().GetReqnrollSettings(packages);

        result.Should().NotBeNull();
        result!.Traits.HasFlag(ReqnrollProjectTraits.MsBuildGeneration).Should().BeTrue();
        result.Traits.HasFlag(ReqnrollProjectTraits.CucumberExpression).Should().BeTrue();
        result.Traits.HasFlag(ReqnrollProjectTraits.LegacySpecFlow).Should().BeFalse();
    }

    [Fact]
    public void GetReqnrollSettings_marks_a_legacy_SpecFlow_package_with_the_LegacySpecFlow_trait()
    {
        var packages = new[]
        {
            new NuGetPackageReference("SpecFlow", new NuGetVersion("2.4.1", "2.4.1"), SpecFlowLegacyPackagePath)
        };

        var result = CreateSut().GetReqnrollSettings(packages);

        result.Should().NotBeNull();
        result!.Traits.HasFlag(ReqnrollProjectTraits.LegacySpecFlow).Should().BeTrue();
    }

    [Fact]
    public void GetReqnrollSettings_infers_DesignTimeFeatureFileGeneration_for_a_pre_3_0_SpecFlow_package_with_no_MsBuild_generation_or_XUnit_adapter()
    {
        // CreateReqnrollSettings' inference branch: LegacySpecFlow + version < 3.0 + neither
        // MsBuildGeneration nor XUnitAdapter already set + a resolvable generator folder together
        // imply the older design-time .feature.cs generation flow was in play.
        var packages = new[]
        {
            new NuGetPackageReference("SpecFlow", new NuGetVersion("2.4.1", "2.4.1"), SpecFlowLegacyPackagePath)
        };

        var result = CreateSut().GetReqnrollSettings(packages);

        result.Should().NotBeNull();
        result!.GeneratorFolder.Should().NotBeNull();
        result.Traits.HasFlag(ReqnrollProjectTraits.DesignTimeFeatureFileGeneration).Should().BeTrue();
    }

    [Fact]
    public void GetReqnrollSettings_sets_the_generator_folder_from_the_package_install_path()
    {
        var packages = new[]
        {
            new NuGetPackageReference("Reqnroll", new NuGetVersion("2.4.1", "2.4.1"), ReqnrollPackagePath)
        };

        var result = CreateSut().GetReqnrollSettings(packages);

        result.Should().NotBeNull();
        result!.GeneratorFolder.Should().Be(System.IO.Path.Combine(ReqnrollPackagePath, "tools"));
    }
}
