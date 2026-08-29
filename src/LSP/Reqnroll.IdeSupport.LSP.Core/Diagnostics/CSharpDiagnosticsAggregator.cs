using Reqnroll.IdeSupport.LSP.Core.Bindings;

namespace Reqnroll.IdeSupport.LSP.Core.Diagnostics;

/// <inheritdoc cref="ICSharpDiagnosticsAggregator"/>
public sealed class CSharpDiagnosticsAggregator : ICSharpDiagnosticsAggregator
{
    /// <inheritdoc/>
    public IReadOnlyList<CSharpBindingDiagnostic> Aggregate(ProjectBindingRegistry registry, string filePath)
    {
        var bindingsInFile = registry.StepDefinitions
            .Cast<ProjectBinding>()
            .Concat(registry.Hooks)
            .Where(b => ProjectBindingRegistry.IsSameSourceFile(b.Implementation.SourceLocation?.SourceFile, filePath))
            .Where(b => b.Implementation.SourceLocation is not null);

        var diagnostics = new List<CSharpBindingDiagnostic>();

        // Group by the effective diagnostic location: ErrorLocation when the failure is
        // attribute-specific (a malformed step expression or scope tag expression — see
        // ProjectBinding.ErrorLocation's remarks), otherwise the method identifier's own
        // location. A method with several step-definition/hook attributes produces one
        // ProjectBinding per attribute; when their errors are all structural (ErrorLocation
        // null), they share the method's SourceLocation and correctly collapse into one
        // diagnostic here, but an attribute-specific error carries its own distinct location and
        // is never merged with another attribute's.
        // Keyed by (line, column) rather than the location object itself -- SourceLocation has no
        // value equality, and unlike Implementation.SourceLocation (constructed once per method
        // and shared across every attribute on it), ErrorLocation is a fresh instance per
        // attribute, so relying on reference equality here would be fragile.
        foreach (var group in bindingsInFile.GroupBy(b =>
                 {
                     var loc = b.ErrorLocation ?? b.Implementation.SourceLocation!;
                     return (loc.SourceFileLine, loc.SourceFileColumn);
                 }))
        {
            var errors = group
                .Select(b => b.Error)
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (errors.Length == 0)
                continue;

            var location = group.First().ErrorLocation ?? group.First().Implementation.SourceLocation!;
            diagnostics.Add(new CSharpBindingDiagnostic(
                string.Join("\n", errors), location, GherkinDiagnosticSeverity.Error));
        }

        return diagnostics;
    }
}
