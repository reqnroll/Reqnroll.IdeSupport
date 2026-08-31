#nullable enable

namespace Reqnroll.IdeSupport.LSP.Core.FindUnusedStepDefinitions;

/// <summary>One step-definition binding expression that has zero matching steps across the workspace.</summary>
/// <param name="ProjectName">Short name of the project that owns the binding.</param>
/// <param name="ClassName">Declaring class name.</param>
/// <param name="MethodName">Method name, without parameters or return type.</param>
/// <param name="BindingExpression">The expression as authored, or null for a method-name-style binding.</param>
/// <param name="SourceFile">
/// The binding's source file <b>as a path that can be opened on this machine</b>, or
/// <see langword="null"/> when no such path exists — see <paramref name="IsResolved"/>.
/// </param>
/// <param name="SourceLine">1-based line of the binding method.</param>
/// <param name="SourceColumn">1-based column of the binding method.</param>
/// <param name="IsResolved">
/// Whether <paramref name="SourceFile"/> names a file that exists here. <see langword="false"/> when
/// the assembly was built somewhere else and its recorded source path could not be mapped onto this
/// machine (issue #540) — a devcontainer or CI build, an external binding package, another machine.
/// </param>
/// <param name="RecordedSourceFile">
/// The source path exactly as binding discovery recorded it. Null when it is the same as
/// <paramref name="SourceFile"/>; populated whenever that path was remapped or could not be
/// resolved, because it is the only thing that explains an unopenable entry to a human.
/// </param>
public sealed record UnusedStepDefinition(
    string? ProjectName,
    string ClassName,
    string MethodName,
    string? BindingExpression,
    string? SourceFile,
    int SourceLine,   // 1-based
    int SourceColumn, // 1-based
    bool IsResolved = true,
    string? RecordedSourceFile = null
);
