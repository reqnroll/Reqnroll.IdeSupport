#nullable enable
using Reqnroll.IdeSupport.Common.ProjectSystem;

namespace Reqnroll.IdeSupport.LSP.Server.Telemetry;

/// <summary>
/// Infers a project's unit test framework and test platform from its NuGet package references
/// (issue #874), for the <c>ProjectCharacteristics</c> event. Pure; the result is a member of a
/// small closed set, never a package name or version.
/// </summary>
/// <remarks>
/// Framework: the Reqnroll test-framework adapter package (<c>Reqnroll.MsTest</c>, ...) decides
/// when present, since it is required by Reqnroll and is unambiguous even when the reference list
/// is the resolved closure (it carries the framework package transitively). Only when no adapter
/// is referenced do the framework packages themselves decide. Distinct frameworks give
/// <see cref="Multiple"/>; none found gives <see langword="null"/> (unknown, not "none" — Rider
/// sends an empty reference list).
/// Platform: package references cannot express the MSBuild switches that select Microsoft Testing
/// Platform for MSTest/xUnit/NUnit, so this is best effort: TUnit is always MTP, otherwise
/// <c>Microsoft.NET.Test.Sdk</c> means VSTest, and anything else is unknown.
/// </remarks>
internal static class UnitTestFrameworkDetector
{
    internal const string MSTest = "MSTest";
    internal const string XUnit = "xUnit";
    internal const string NUnit = "NUnit";
    internal const string TUnit = "TUnit";
    internal const string Multiple = "Multiple";
    internal const string VSTest = "VSTest";
    internal const string Mtp = "MTP";

    private const string VSTestSdkPackage = "Microsoft.NET.Test.Sdk";

    private static readonly Dictionary<string, string> AdapterPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Reqnroll.MsTest"] = MSTest,
        ["Reqnroll.xUnit"] = XUnit,
        ["Reqnroll.xunit.v3"] = XUnit,
        ["Reqnroll.NUnit"] = NUnit,
        ["Reqnroll.TUnit"] = TUnit,
    };

    private static readonly Dictionary<string, string> FrameworkPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTest"] = MSTest,
        ["MSTest.TestFramework"] = MSTest,
        ["MSTest.TestAdapter"] = MSTest,
        ["xunit"] = XUnit,
        ["xunit.core"] = XUnit,
        ["xunit.v3"] = XUnit,
        ["xunit.v3.core"] = XUnit,
        ["NUnit"] = NUnit,
        ["TUnit"] = TUnit,
        ["TUnit.Core"] = TUnit,
    };

    /// <summary>The detected framework and platform; each is <see langword="null"/> when unknown.</summary>
    internal readonly record struct Result(string? Framework, string? Platform);

    internal static Result Detect(IEnumerable<NuGetPackageReference>? packageReferences)
    {
        var names = (packageReferences ?? [])
            .Select(p => p?.PackageName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var framework = Classify(names, AdapterPackages) ?? Classify(names, FrameworkPackages);

        string? platform = null;
        if (framework is TUnit || names.Contains("TUnit"))
            platform = Mtp;
        else if (names.Contains(VSTestSdkPackage))
            platform = VSTest;

        return new Result(framework, platform);
    }

    private static string? Classify(HashSet<string> names, Dictionary<string, string> map)
    {
        var found = map.Where(kv => names.Contains(kv.Key)).Select(kv => kv.Value).Distinct().ToList();
        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => Multiple,
        };
    }
}
