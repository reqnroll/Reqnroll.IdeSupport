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

        // Group by method identity (the identifier's own location) rather than by binding: a
        // method with several step-definition/hook attributes produces one ProjectBinding per
        // attribute, all sharing the same SourceLocation, and should surface as one diagnostic.
        foreach (var group in bindingsInFile.GroupBy(b =>
                     (b.Implementation.SourceLocation!.SourceFileLine, b.Implementation.SourceLocation.SourceFileColumn)))
        {
            var errors = group
                .Select(b => b.Error)
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (errors.Length == 0)
                continue;

            var location = group.First().Implementation.SourceLocation!;
            diagnostics.Add(new CSharpBindingDiagnostic(
                string.Join("\n", errors), location, GherkinDiagnosticSeverity.Error));
        }

        return diagnostics;
    }
}
