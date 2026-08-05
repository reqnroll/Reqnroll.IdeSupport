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
    string MethodName);
