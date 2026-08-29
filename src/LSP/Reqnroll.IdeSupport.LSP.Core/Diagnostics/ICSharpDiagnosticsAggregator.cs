using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <summary>
/// Produces binding-validation diagnostics for a single <c>.cs</c> file from a project's binding
/// registry (issue #514). The registry already carries every binding's <c>Error</c> — set by
/// <see cref="Parsing.CSharp.StepDefinitionFileParser"/>'s structural validation for Roslyn
/// discovery, or by the connector for reflection-based discovery — so this aggregator only reads
/// and groups that data; it runs no validation of its own.
/// </summary>
public interface ICSharpDiagnosticsAggregator
{
    /// <summary>
    /// Produces one diagnostic per distinct binding-method location in <paramref name="filePath"/>
    /// that carries an <see cref="ProjectBinding.Error"/>. A method with multiple step-definition
    /// or hook attributes collapses to a single diagnostic at that method's location — the
    /// registry holds one binding per attribute, all sharing one <c>SourceLocation</c>, so without
    /// this the same error would otherwise be reported once per attribute (issue #514 spike
    /// finding: observed as triplicate diagnostics on one squiggle for a three-attribute method).
    /// </summary>
    IReadOnlyList<CSharpBindingDiagnostic> Aggregate(ProjectBindingRegistry registry, string filePath);
}
