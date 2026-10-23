#nullable enable

using Reqnroll.IdeSupport.Common.Configuration.Model;
using Reqnroll.IdeSupport.LSP.Core.Matching;

namespace Reqnroll.IdeSupport.LSP.Core.Scaffolding;

/// <summary>
/// Converts a set of undefined <see cref="StepBindingMatch"/> entries into a deduplicated,
/// ordered list of <see cref="StepSkeletonDescriptor"/> ready for rendering.
/// </summary>
public interface IStepScaffoldService
{
    /// <summary>
    /// Builds step skeleton descriptors for the given undefined steps.
    /// Duplicates (same Block + expression) are collapsed to one descriptor.
    /// </summary>
    IReadOnlyList<StepSkeletonDescriptor> BuildDescriptors(
        IEnumerable<StepBindingMatch> undefinedSteps,
        SnippetExpressionStyle        style);
}
