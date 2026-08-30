#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;

/// <summary>One step-definition binding expression that has zero matching steps across the workspace.</summary>
public sealed record UnusedStepDefinition(
    string? ProjectName,
    string ClassName,
    string MethodName,
    string? BindingExpression,
    string? SourceFile,
    int SourceLine,   // 1-based
    int SourceColumn  // 1-based
);
