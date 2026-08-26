#nullable enable

namespace Reqnroll.IdeSupport.VisualStudio.RunTestCodeLens;

/// <summary>
/// One resolved generated-test-method target for a <c>.feature</c> scenario/Scenario Outline line
/// (design doc §3/§4, issue #262), as consumed by the classic Run CodeLens bridge.
/// </summary>
public sealed record RunTestTargetEntry(
    /// <summary>0-based line the target's scenario/Outline header is on.</summary>
    int Line,
    /// <summary>The owning project's build-output assembly path (needed for VS Test Explorer's own <c>TestMethodIdentifier</c> — the LSP protocol itself has no notion of it).</summary>
    string OutputAssemblyPath,
    /// <summary>The generated test class's full name, e.g. <c>Discovery_PlatformCompatibilityFeature</c>.</summary>
    string DeclaringTypeFullName,
    /// <summary>The generated method name.</summary>
    string MethodName,
    /// <summary>
    /// True when <see cref="Line"/> is a <c>Scenario Outline:</c> header rather than a plain
    /// <c>Scenario:</c> — used to choose "Run Scenario" vs "Run Scenarios" wording for the CodeLens
    /// label, since a row-tests Outline still collapses to one <see cref="MethodName"/> like a plain
    /// scenario would (see <c>ScenarioTestTargetResolver.ResolveExactMethod</c>'s remarks), so the
    /// method/target shape alone can't distinguish the two cases.
    /// </summary>
    bool IsScenarioOutline = false);
