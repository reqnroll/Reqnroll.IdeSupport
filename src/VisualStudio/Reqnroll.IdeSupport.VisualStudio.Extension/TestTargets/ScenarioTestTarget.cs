#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.Extension.TestTargets;

/// <summary>
/// One generated test method (or one row of a row-tests-parameterized method) that a
/// <c>.feature</c> scenario/Scenario Outline resolves to. Mirrors <c>ScenarioTestTargetDto</c> in
/// <c>ResolveTestTargetsResponse.cs</c> on the server (design doc §3/§4, issue #262).
/// </summary>
public sealed record ScenarioTestTarget(
    string DeclaringTypeFullName,
    string MethodName,
    bool IsParameterized,
    int? RowIndex);
