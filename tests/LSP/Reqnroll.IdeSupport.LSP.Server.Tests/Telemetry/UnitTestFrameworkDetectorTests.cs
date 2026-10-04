#nullable enable

using Reqnroll.IdeSupport.Common.ProjectSystem;
using Reqnroll.IdeSupport.LSP.Server.Telemetry;

namespace Reqnroll.IdeSupport.LSP.Server.Tests.Telemetry;

public class UnitTestFrameworkDetectorTests
{
    private static UnitTestFrameworkDetector.Result Detect(params string[] packages) =>
        UnitTestFrameworkDetector.Detect(packages.Select(p => new NuGetPackageReference(p, new NuGetVersion("1.0.0", "1.0.0"), null!)));

    [Theory]
    [InlineData("Reqnroll.MsTest", "MSTest")]
    [InlineData("Reqnroll.xUnit", "xUnit")]
    [InlineData("Reqnroll.xunit.v3", "xUnit")]
    [InlineData("Reqnroll.NUnit", "NUnit")]
    [InlineData("Reqnroll.TUnit", "TUnit")]
    [InlineData("MSTest.TestFramework", "MSTest")]
    [InlineData("xunit", "xUnit")]
    [InlineData("xunit.v3", "xUnit")]
    [InlineData("NUnit", "NUnit")]
    [InlineData("TUnit", "TUnit")]
    public void Detects_the_framework_from_an_adapter_or_framework_package(string package, string expected)
    {
        Detect(package).Framework.Should().Be(expected);
    }

    [Fact]
    public void Package_names_are_matched_case_insensitively()
    {
        Detect("reqnroll.nunit", "NUNIT").Framework.Should().Be("NUnit");
    }

    [Fact]
    public void The_reqnroll_adapter_decides_over_transitive_framework_packages()
    {
        // The resolved closure of Reqnroll.xUnit can pull in unrelated framework packages.
        Detect("Reqnroll.xUnit", "xunit", "NUnit").Framework.Should().Be("xUnit");
    }

    [Fact]
    public void Distinct_frameworks_without_an_adapter_are_reported_as_Multiple()
    {
        Detect("xunit", "NUnit").Framework.Should().Be("Multiple");
        Detect("Reqnroll.xUnit", "Reqnroll.NUnit").Framework.Should().Be("Multiple");
    }

    [Fact]
    public void Several_packages_of_the_same_framework_are_not_Multiple()
    {
        Detect("xunit", "xunit.core").Framework.Should().Be("xUnit");
    }

    [Fact]
    public void No_recognised_package_is_unknown_not_a_value()
    {
        var result = Detect("Newtonsoft.Json");

        result.Framework.Should().BeNull();
        result.Platform.Should().BeNull();
        UnitTestFrameworkDetector.Detect(null).Should().Be(new UnitTestFrameworkDetector.Result(null, null));
    }

    [Fact]
    public void TUnit_is_always_the_testing_platform()
    {
        Detect("Reqnroll.TUnit").Platform.Should().Be("MTP");
        Detect("TUnit", "Microsoft.NET.Test.Sdk").Platform.Should().Be("MTP");
    }

    [Fact]
    public void The_VSTest_sdk_package_means_VSTest_otherwise_the_platform_is_unknown()
    {
        Detect("Reqnroll.NUnit", "Microsoft.NET.Test.Sdk").Platform.Should().Be("VSTest");
        Detect("Reqnroll.NUnit").Platform.Should().BeNull();
    }
}
