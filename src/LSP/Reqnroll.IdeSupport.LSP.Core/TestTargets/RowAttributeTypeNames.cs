namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// The attribute type each Reqnroll test-framework provider emits per <c>Examples:</c> row on a
/// row-tests-parameterized generated method, decompiled from each provider's <c>SetRow</c> — see
/// design doc §2. MSTest uses the same attribute (<c>DataRowAttribute</c>) for both
/// <c>MsTestV2GeneratorProvider</c> and <c>MsTestV4GeneratorProvider</c>, so no
/// <c>TargetMsTestVersion</c> split is needed here.
/// </summary>
public static class RowAttributeTypeNames
{
    /// <summary>Row-attribute simple type name (without namespace) per <see cref="TestFramework"/>.</summary>
    public static readonly IReadOnlyDictionary<TestFramework, string> ByFramework = new Dictionary<TestFramework, string>
    {
        [TestFramework.XUnit] = "InlineDataAttribute",
        [TestFramework.XUnitV3] = "InlineDataAttribute",
        [TestFramework.NUnit3] = "TestCaseAttribute",
        [TestFramework.TUnit] = "ArgumentsAttribute",
        [TestFramework.MsTest] = "DataRowAttribute",
    };
}
