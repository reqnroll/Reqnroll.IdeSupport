using ReqnrollConnector.Utils;

namespace Reqnroll.IdeSupport.LSP.Connector.Tests.Utils;

/// <summary>
/// Covers <see cref="FrameworkMonikerConverter"/>'s mapping from a full <c>FrameworkName</c>
/// string (as produced by <c>TargetFrameworkAttribute</c>) to the short folder-name form used to
/// key NuGet cache lookups (an alternative to <c>NuGet.Frameworks</c>'s
/// <c>NuGetFramework.Parse</c>/<c>GetShortFolderName</c>, to avoid that dependency).
/// </summary>
public class FrameworkMonikerConverterTests
{
    [Theory]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData(".NETCoreApp,Version=v9.0", "net9.0")]
    [InlineData(".NETCoreApp,Version=v10.0", "net10.0")]
    [InlineData(".NETCoreApp,Version=v5.0", "net5.0")]
    [InlineData(".NETCoreApp,Version=v6.0", "net6.0")]
    public void GetShortFrameworkName_maps_dotnet_5_plus_to_netX_Y(string fullFrameworkName, string expected)
    {
        FrameworkMonikerConverter.GetShortFrameworkName(fullFrameworkName).Should().Be(expected);
    }

    [Fact]
    public void GetShortFrameworkName_maps_dotnet_core_3_x_and_earlier_to_netcoreapp_prefix()
    {
        FrameworkMonikerConverter.GetShortFrameworkName(".NETCoreApp,Version=v3.1").Should().Be("netcoreapp3.1");
    }

    [Theory]
    [InlineData(".NETFramework,Version=v4.8.1", "net481")]
    [InlineData(".NETFramework,Version=v4.7.2", "net472")]
    [InlineData(".NETFramework,Version=v4.6.2", "net462")]
    [InlineData(".NETFramework,Version=v4.8", "net48")]
    [InlineData(".NETFramework,Version=v4.0", "net40")]
    public void GetShortFrameworkName_maps_dotnet_framework_monikers_to_netXXX(string fullFrameworkName, string expected)
    {
        FrameworkMonikerConverter.GetShortFrameworkName(fullFrameworkName).Should().Be(expected);
    }

    [Fact]
    public void GetShortFrameworkName_maps_netstandard()
    {
        FrameworkMonikerConverter.GetShortFrameworkName(".NETStandard,Version=v2.1").Should().Be("netstandard2.1");
    }

    [Fact]
    public void GetShortFrameworkName_maps_portable_with_profile()
    {
        FrameworkMonikerConverter.GetShortFrameworkName(".NETPortable,Version=v4.5,Profile=Profile78")
            .Should().Be("portable-Profile78");
    }

    [Fact]
    public void GetShortFrameworkName_throws_NotSupportedException_for_an_unrecognized_identifier()
    {
        var act = () => FrameworkMonikerConverter.GetShortFrameworkName("Silverlight,Version=v5.0");

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void GetShortFrameworkName_throws_for_a_malformed_moniker_string()
    {
        var act = () => FrameworkMonikerConverter.GetShortFrameworkName("not-a-framework-name");

        act.Should().Throw<Exception>();
    }

    // ── TryGetShortFrameworkName ─────────────────────────────────────────────────

    [Fact]
    public void TryGetShortFrameworkName_returns_true_and_the_short_name_for_a_supported_moniker()
    {
        var success = FrameworkMonikerConverter.TryGetShortFrameworkName(".NETCoreApp,Version=v10.0", out var shortName);

        success.Should().BeTrue();
        shortName.Should().Be("net10.0");
    }

    [Fact]
    public void TryGetShortFrameworkName_returns_false_and_null_for_an_unsupported_moniker()
    {
        var success = FrameworkMonikerConverter.TryGetShortFrameworkName("Silverlight,Version=v5.0", out var shortName);

        success.Should().BeFalse();
        shortName.Should().BeNull();
    }

    [Fact]
    public void TryGetShortFrameworkName_returns_false_for_a_malformed_moniker_string_rather_than_throwing()
    {
        var success = FrameworkMonikerConverter.TryGetShortFrameworkName("garbage", out var shortName);

        success.Should().BeFalse();
        shortName.Should().BeNull();
    }
}
