#nullable enable

using Reqnroll.IdeSupport.Common.Configuration;
using Reqnroll.IdeSupport.LSP.Core.Bindings;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Scaffolding;

/// <summary>
/// Collects undefined steps from the match cache and produces a deduplicated list of
/// <see cref="StepSkeletonDescriptor"/> objects using <see cref="StepSkeletonRenderer"/>.
/// </summary>
public sealed class StepScaffoldService : IStepScaffoldService
{
    /// <summary>Collects undefined steps and produces a deduplicated list of step skeleton descriptors, one per distinct step expression.</summary>
    public IReadOnlyList<StepSkeletonDescriptor> BuildDescriptors(
        IEnumerable<StepBindingMatch> undefinedSteps,
        SnippetExpressionStyle        style)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<StepSkeletonDescriptor>();

        foreach (var match in undefinedSteps)
        {
            // Each undefined step carries at least one Undefined MatchResultItem.
            foreach (var item in match.Result.Items)
            {
                if (item.Type != MatchResultType.Undefined) continue;
                if (item.UndefinedStep is not { } step) continue;

                // A step consisting of only a keyword (e.g. "Given" with no following text) has
                // no step text to build a skeleton from — offering a code action here would
                // generate a meaningless `[Given(@"")]`-style binding (issue #622).
                if (string.IsNullOrWhiteSpace(step.StepText)) continue;

                var descriptor = StepSkeletonRenderer.BuildDescriptor(step, style);

                // Dedup by (Block, expression).
                if (seen.Add(descriptor.DeduplicationKey))
                    result.Add(descriptor);
            }
        }

        // Order: Given < When < Then, then by appearance (insertion order preserved within block).
        return result
            .OrderBy(d => (int)d.Block)
            .ToList()
            .AsReadOnly();
    }
}
