using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <summary>
/// A protocol-agnostic diagnostic for a binding-validation error found in a <c>.cs</c> file.
/// The server layer converts this to an LSP <c>Diagnostic</c> before pushing
/// <c>textDocument/publishDiagnostics</c> (issue #514).
/// </summary>
/// <param name="Message">Human-readable validation failure, from <see cref="Bindings.ProjectBinding.Error"/>.</param>
/// <param name="Location">The binding method's source span (identifier start through end).</param>
/// <param name="Severity">
/// Reuses <see cref="GherkinDiagnosticSeverity"/> — a protocol-agnostic Error/Warning shape with
/// no Gherkin-specific meaning despite the name; not worth introducing a duplicate enum for.
/// </param>
public record CSharpBindingDiagnostic(
    string Message,
    SourceLocation Location,
    GherkinDiagnosticSeverity Severity);
