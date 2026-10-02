namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// The attribute types Reqnroll's test-framework providers emit per <c>Examples:</c> row on a
/// row-tests-parameterized generated method, decompiled from each provider's <c>SetRow</c> — see
/// design doc §2: <c>InlineDataAttribute</c> (xUnit, xUnit.v3), <c>TestCaseAttribute</c> (NUnit3),
/// <c>ArgumentsAttribute</c> (TUnit), <c>DataRowAttribute</c> (MSTest — the same attribute from both
/// <c>MsTestV2GeneratorProvider</c> and <c>MsTestV4GeneratorProvider</c>, so the
/// <c>TargetMsTestVersion</c> split never matters here).
/// </summary>
/// <remarks>
/// Kept as one union rather than keyed by framework: the resolver reads the generated code-behind
/// as ground truth and doesn't need to know which provider wrote it (issue #455).
/// </remarks>
public static class RowAttributeTypeNames
{
    /// <summary>Row-attribute simple type names (without namespace), across every supported provider.</summary>
    public static readonly IReadOnlyCollection<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "InlineDataAttribute",
        "TestCaseAttribute",
        "ArgumentsAttribute",
        "DataRowAttribute",
    };
}
