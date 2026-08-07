namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// Detects a project's active Reqnroll test-framework provider from its referenced NuGet package
/// IDs — the same "which <c>Reqnroll.&lt;Framework&gt;.Generator.ReqnrollPlugin</c> is wired up"
/// question design doc §2/§3 flags as needing resolution, simplified here to package-reference
/// detection (as opposed to resolving the exact provider type via <c>UseUnitTestProvider</c>).
/// </summary>
public static class TestFrameworkDetection
{
    // Reqnroll.xUnit.v3 must be checked before Reqnroll.xUnit, since "Reqnroll.xUnit.v3"
    // also contains "Reqnroll.xUnit" as a prefix.
    private static readonly (string PackageId, TestFramework Framework)[] KnownPackages =
    {
        ("Reqnroll.xUnit.v3", TestFramework.XUnitV3),
        ("Reqnroll.xUnit", TestFramework.XUnit),
        ("Reqnroll.NUnit", TestFramework.NUnit3),
        ("Reqnroll.MsTest", TestFramework.MsTest),
        ("Reqnroll.TUnit", TestFramework.TUnit),
    };

    /// <summary>Returns the detected test framework, or <see langword="null"/> if none of the known Reqnroll test-framework packages are referenced.</summary>
    public static TestFramework? Detect(IReadOnlyCollection<string> projectPackageIds)
    {
        foreach (var (packageId, framework) in KnownPackages)
        {
            foreach (var referenced in projectPackageIds)
            {
                if (string.Equals(referenced, packageId, StringComparison.OrdinalIgnoreCase))
                    return framework;
            }
        }
        return null;
    }
}
