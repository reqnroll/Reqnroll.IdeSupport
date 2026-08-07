namespace Reqnroll.IdeSupport.LSP.Core.TestTargets;

/// <summary>
/// One generated test method (or one row of a row-tests-parameterized method) that a <c>.feature</c>
/// scenario or Scenario Outline example row resolves to. See docs/Test-Runner-Integration-Design.md §3.
/// </summary>
/// <param name="DeclaringTypeFullName">The generated test class, e.g. <c>Discovery_PlatformCompatibilityFeature</c>.</param>
/// <param name="MethodName">The generated method name.</param>
/// <param name="IsParameterized">
/// <see langword="true"/> when <paramref name="MethodName"/> is a row-tests Scenario Outline method
/// (multiple targets share the same method, distinguished by <paramref name="RowIndex"/>).
/// </param>
/// <param name="RowArguments">
/// The row's argument values by column header, when known (ordinary Outline case, correlated
/// positionally against the <c>.feature</c> file's own <c>Examples:</c> rows). <see langword="null"/>
/// when not resolving a specific row, or when the row can't be correlated to a visible
/// <c>.feature</c>-file row (e.g. a <c>Reqnroll.ExternalData</c>-style AST-injected row — see design
/// doc §2's "AST-transforming generator plugins" subsection).
/// </param>
/// <param name="RowIndex">
/// The 0-based position of this target among the method's row-attribute instances, present only for
/// a row-tests-parameterized target. <see langword="null"/> otherwise.
/// </param>
public sealed record ScenarioTestTarget(
    string DeclaringTypeFullName,
    string MethodName,
    bool IsParameterized,
    IReadOnlyDictionary<string, string>? RowArguments,
    int? RowIndex);
