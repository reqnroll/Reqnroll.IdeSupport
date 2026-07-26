using System;
using Reqnroll.IdeSupport.LSP.Core.Documents;

namespace Reqnroll.IdeSupport.LSP.Core.Bindings;

/// <summary>
/// Shared "does this source location belong to this binding" heuristic, used both when
/// resolving a single binding at a location (<see cref="ProjectBindingRegistry"/>) and when
/// checking whether a location has any covering binding at all (used by the LSP server's
/// registry router for watched-file gating). Previously duplicated by hand in both places.
/// </summary>
public static class BindingLocationMatcher
{
    /// <summary>
    /// Returns whether <paramref name="query"/> covers <paramref name="binding"/>'s source
    /// location: the exact attribute line or the method identifier line for syntax-discovered
    /// bindings (<see cref="ProjectStepDefinitionBinding.AttributeSourceLine"/> known), or a
    /// small heuristic window above the recorded method location for connector-discovered
    /// bindings (PDB sequence points, which don't carry an attribute line). Column is
    /// intentionally ignored: Gherkin/C# line-oriented lookups like this should match anywhere
    /// on the relevant line(s), not an exact column.
    /// </summary>
    public static bool CoversQuery(ProjectStepDefinitionBinding binding, SourceLocation query)
    {
        var loc = binding.Implementation.SourceLocation;
        if (loc == null)
            return false;

        if (!string.Equals(loc.SourceFile, query.SourceFile, StringComparison.OrdinalIgnoreCase))
            return false;

        // AST-based: when the attribute line is known, match it exactly or the method line.
        if (binding.AttributeSourceLine.HasValue)
        {
            return query.SourceFileLine == binding.AttributeSourceLine.Value
                   || query.SourceFileLine == loc.SourceFileLine;
        }

        // Fallback heuristic for connector-discovered bindings (PDB sequence points).
        var endLine = loc.SourceFileEndLine ?? loc.SourceFileLine;
        const int attributeLeeway = 2;
        return query.SourceFileLine >= (loc.SourceFileLine - attributeLeeway)
               && query.SourceFileLine <= endLine;
    }
}
