using System.Collections.Generic;
using Gherkin.Ast;

namespace Reqnroll.IdeSupport.LSP.Core.Parsing.Gherkin;

/// <summary>
/// Extracts tag names (e.g. <c>@smoke</c>) from a parsed <see cref="IdeSupportTag"/> collection
/// and counts how often each is used. The collection is the flat, position-sorted set produced by
/// <see cref="IdeSupportTagParser"/> — it holds parents and children side by side, so this counts
/// by iterating the collection itself, never by recursing into <see cref="IdeSupportTag.ChildTags"/>
/// (recursion would visit every node twice). The <see cref="IdeSupportTagTypes.Tag"/> nodes carry
/// the original <see cref="Tag"/> payloads; feeding documents parsed against
/// <see cref="Reqnroll.IdeSupport.LSP.Core.Bindings.ProjectBindingRegistry.Invalid"/> keeps step
/// matching out of the picture entirely, since tag completion only needs the structure.
/// </summary>
public static class GherkinTagNameCollector
{
    /// <summary>Counts every <c>Tag</c> node in the given flat tag collection.</summary>
    public static IReadOnlyDictionary<string, int> CollectCounts(IReadOnlyCollection<IdeSupportTag> tags)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (tag.Type == IdeSupportTagTypes.Tag &&
                tag.Data is Tag gherkinTag &&
                !string.IsNullOrEmpty(gherkinTag.Name))
            {
                counts[gherkinTag.Name] = counts.TryGetValue(gherkinTag.Name, out var existing)
                    ? existing + 1
                    : 1;
            }
        }
        return counts;
    }
}
